using Autofac;
using HashStormCore.Mappings;
using HashStormCore.Blockchain.Bitcoin.Configuration;
using HashStormCore.Blockchain.Bitcoin.DaemonResponses;
using HashStormCore.Configuration;
using HashStormCore.Extensions;
using HashStormCore.Messaging;
using HashStormCore.Mining;
using HashStormCore.Payments;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;
using HashStormCore.Rpc;
using HashStormCore.Time;
using HashStormCore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Block = HashStormCore.Persistence.Model.Block;
using Contract = HashStormCore.Contracts.Contract;
using static HashStormCore.Util.ActionUtils;

namespace HashStormCore.Blockchain.Bitcoin;

[CoinFamily(CoinFamily.Bitcoin, CoinFamily.Nexa)]
public class BitcoinPayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    public BitcoinPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IObjectMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);

        this.ctx = ctx;
    }

    protected readonly IComponentContext ctx;
    protected RpcClient rpcClient;
    protected BitcoinPoolConfigExtra extraPoolConfig;
    protected BitcoinDaemonEndpointConfigExtra extraPoolEndpointConfig;
    protected BitcoinPoolPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    protected const int WalletUnlockSeconds = 5;

    private int payoutDecimalPlaces = 4;
    private CoinTemplate coin;
    private int minConfirmations;

    protected override string LogCategory => "Bitcoin Payout Handler";

    #region IPayoutHandler

    public virtual Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;

        extraPoolConfig = pc.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>();
        extraPoolEndpointConfig = pc.Extra.SafeExtensionDataAs<BitcoinDaemonEndpointConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<BitcoinPoolPaymentProcessingConfigExtra>();

        coin = poolConfig.Template.As<CoinTemplate>();
        if(coin is BitcoinTemplate bitcoinTemplate)
        {
            minConfirmations = extraPoolEndpointConfig?.MinimumConfirmations ?? bitcoinTemplate.CoinbaseMinConfimations ?? BitcoinConstants.CoinbaseMinConfimations;
            payoutDecimalPlaces = bitcoinTemplate.PayoutDecimalPlaces ?? 4;
        }
        else
            minConfirmations = extraPoolEndpointConfig?.MinimumConfirmations ?? BitcoinConstants.CoinbaseMinConfimations;

        logger = LogUtil.GetPoolScopedLogger(typeof(BitcoinPayoutHandler), pc);

        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
        rpcClient = new RpcClient(pc.Daemons.First(), jsonSerializerSettings, messageBus, pc.Id);

        return Task.CompletedTask;
    }

    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();

        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            // build command batch (block.TransactionConfirmationData is the hash of the blocks coinbase transaction)
            var batch = page.Select(block => new RpcRequest(BitcoinCommands.GetTransaction,
                new[] { block.TransactionConfirmationData })).ToArray();

            // execute batch
            var results = await rpcClient.ExecuteBatchAsync(logger, ct, batch);

            for(var j = 0; j < results.Length; j++)
            {
                var cmdResult = results[j];

                var transactionInfo = cmdResult.Response?.ToObject<Transaction>();
                var block = page[j];

                // check error
                if(cmdResult.Error != null)
                {
                    // Code -5 interpreted as "orphaned"
                    if(cmdResult.Error.Code == -5)
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to daemon error {cmdResult.Error.Code}");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }

                    else
                        logger.Warn(() => $"[{LogCategory}] Daemon reports error '{cmdResult.Error.Message}' (Code {cmdResult.Error.Code}) for transaction {page[j].TransactionConfirmationData}");
                }

                // missing transaction details are interpreted as "orphaned"
                else if(transactionInfo?.Details == null || transactionInfo.Details.Length == 0)
                {
                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;
                    result.Add(block);

                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to missing tx details");

                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                }

                else
                {
                    switch(transactionInfo.Details[0].Category)
                    {
                        case "immature":
                            // update progress
                            block.ConfirmationProgress = Math.Min(1.0d, (double) transactionInfo.Confirmations / minConfirmations);
                            block.Reward = transactionInfo.Amount;  // update actual block-reward from coinbase-tx
                            result.Add(block);

                            messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);
                            break;

                        case "generate":
                            // matured and spendable coinbase transaction
                            block.Status = BlockStatus.Confirmed;
                            block.ConfirmationProgress = 1;
                            block.Reward = transactionInfo.Amount;  // update actual block-reward from coinbase-tx
                            result.Add(block);

                            logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");

                            messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                            break;

                        default:
                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned. Category: {transactionInfo.Details[0].Category}");

                            block.Status = BlockStatus.Orphaned;
                            block.Reward = 0;
                            result.Add(block);

                            messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                            break;
                    }
                }
            }
        }

        return result.ToArray();
    }

    public virtual async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        // build args
        var amounts = balances
            .Where(x => x.Amount > 0)
            .ToDictionary(x => x.Address, x => Math.Round(x.Amount, payoutDecimalPlaces));

        if(amounts.Count == 0)
            return;

        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

        object[] args;

        var identifier = !string.IsNullOrEmpty(clusterConfig.PaymentProcessing?.CoinbaseString) ?
            clusterConfig.PaymentProcessing.CoinbaseString.Trim() : "HashStormCore";

        var comment = $"{identifier} Payment";

        if(!(extraPoolConfig?.HasBrokenSendMany == true || poolConfig.Template is BitcoinTemplate { HasBrokenSendMany: true }))
        {
            if(extraPoolPaymentProcessingConfig?.MinersPayTxFees == true)
            {
                var subtractFeesFrom = amounts.Keys.ToArray();

                if(!poolConfig.Template.As<BitcoinTemplate>().HasMasterNodes)
                {
                    args = new object[]
                    {
                        string.Empty, // default account
                        amounts, // addresses and associated amounts
                        1, // only spend funds covered by this many confirmations
                        comment, // tx comment
                        subtractFeesFrom, // distribute transaction fee equally over all recipients
                    };
                }

                else
                {
                    args = new object[]
                    {
                        string.Empty, // default account
                        amounts, // addresses and associated amounts
                        1, // only spend funds covered by this many confirmations
                        false, // Whether to add confirmations to transactions locked via InstantSend
                        comment, // tx comment
                        subtractFeesFrom, // distribute transaction fee equally over all recipients
                        false, // use_is: Send this transaction as InstantSend
                        false, // Use anonymized funds only
                    };
                }
            }

            else
            {
                args = new object[]
                {
                    string.Empty, // default account
                    amounts, // addresses and associated amounts
                };
            }

            var didUnlockWallet = false;

            try
            {
                // send command
                tryTransfer:
                var result = await rpcClient.ExecuteAsync<string>(logger, BitcoinCommands.SendMany, ct, args);

                if(result.Error == null)
                {
                    // check result
                    var txId = result.Response;

                    if(string.IsNullOrEmpty(txId))
                        logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} did not return a transaction id!");
                    else
                        logger.Info(() => $"[{LogCategory}] Payment transaction id: {txId}");

                    await PersistPaymentsAsync(balances, txId);

                    NotifyPayoutSuccess(poolConfig.Id, balances, new[]
                    {
                        txId
                    }, null);
                }

                else
                {
                    if(IsWalletUnlockNeeded(result) && !didUnlockWallet)
                    {
                        if(await TryUnlockWalletAsync(ct))
                        {
                            didUnlockWallet = true;
                            goto tryTransfer;
                        }

                        logger.Error(() => $"[{LogCategory}] Wallet is locked and could not be unlocked. Unable to send funds.");
                        NotifyPayoutFailure(poolConfig.Id, balances, "Wallet is locked and could not be unlocked. Unable to send funds.", null);
                    }

                    else
                    {
                        logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}");

                        NotifyPayoutFailure(poolConfig.Id, balances, $"{BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}", null);
                    }
                }
            }
            finally
            {
                await LockWalletIfUnlockedAsync(didUnlockWallet, ct);
            }
        }

        else
        {
            var txFailures = new List<Tuple<KeyValuePair<string, decimal>, Exception>>();
            var successBalances = new Dictionary<Balance, string>();

            foreach(var x in amounts)
            {
                var (address, amount) = x;

                await Guard(async () =>
                {
                    // use a common id for all log entries related to this transfer
                    var transferId = CorrelationIdGenerator.GetNextId();

                    logger.Info(()=> $"[{LogCategory}] [{transferId}] Sending {FormatAmount(amount)} to {address}");

                    var args = new object[]
                    {
                        address,
                        amount,
                    };

                    var didUnlockWallet = false;

                    try
                    {
                        tryTransfer:
                        var result = await rpcClient.ExecuteAsync<string>(logger, BitcoinCommands.SendToAddress, ct, args);

                        // check result
                        var txId = result.Response;

                        if(result.Error != null)
                        {
                            if(IsWalletUnlockNeeded(result) && !didUnlockWallet)
                            {
                                if(await TryUnlockWalletAsync(ct))
                                {
                                    didUnlockWallet = true;
                                    goto tryTransfer;
                                }

                                throw new Exception($"[{transferId}] Wallet is locked and could not be unlocked. Unable to send funds.");
                            }

                            throw new Exception($"[{transferId}] {BitcoinCommands.SendToAddress} returned error: {result.Error.Message} code {result.Error.Code}");
                        }

                        if(string.IsNullOrEmpty(txId))
                            throw new Exception($"[{transferId}] {BitcoinCommands.SendToAddress} did not return a transaction id!");
                        else
                            logger.Info(() => $"[{LogCategory}] [{transferId}] Payment transaction id: {txId}");

                        successBalances.Add(new Balance
                        {
                            PoolId = poolConfig.Id,
                            Address = address,
                            Amount = amount,
                        }, txId);
                    }
                    finally
                    {
                        await LockWalletIfUnlockedAsync(didUnlockWallet, ct);
                    }
                }, ex =>
                {
                    txFailures.Add(Tuple.Create(x, ex));
                });
            }

            if(successBalances.Any())
            {
                await PersistPaymentsAsync(successBalances);

                NotifyPayoutSuccess(poolConfig.Id, successBalances.Keys.ToArray(), successBalances.Values.ToArray(), null);
            }

            if(txFailures.Any())
            {
                var failureBalances = txFailures.Select(x=> new Balance { Amount = x.Item1.Value }).ToArray();
                var error = string.Join(", ", txFailures.Select(x => $"{x.Item1.Key} {FormatAmount(x.Item1.Value)}: {x.Item2.Message}"));

                logger.Error(()=> $"[{LogCategory}] Failed to transfer the following balances: {error}");

                NotifyPayoutFailure(poolConfig.Id, failureBalances, error, null);
            }
        }
    }

    public double AdjustBlockEffort(double effort)
    {
        return effort;
    }

    protected bool IsWalletUnlockNeeded<T>(RpcResponse<T> response)
    {
        return response.Error?.Code == (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED;
    }

    protected async Task<bool> TryUnlockWalletAsync(CancellationToken ct)
    {
        if(string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPassword))
        {
            logger.Error(() => $"[{LogCategory}] Wallet is locked but walletPassword was not configured. Unable to send funds.");
            return false;
        }

        logger.Info(() => $"[{LogCategory}] Unlocking wallet");

        var unlockResult = await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletPassphrase, ct, new[]
        {
            extraPoolPaymentProcessingConfig.WalletPassword,
            (object) WalletUnlockSeconds
        });

        if(unlockResult.Error == null)
            return true;

        logger.Error(() => $"[{LogCategory}] {BitcoinCommands.WalletPassphrase} returned error: {unlockResult.Error.Message} code {unlockResult.Error.Code}");
        return false;
    }

    protected async Task LockWalletIfUnlockedAsync(bool didUnlockWallet, CancellationToken ct)
    {
        if(!didUnlockWallet)
            return;

        logger.Info(() => $"[{LogCategory}] Locking wallet");
        await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletLock, ct);
    }

    #endregion // IPayoutHandler
}
