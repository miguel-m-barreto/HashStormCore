using System.Collections.Concurrent;
using System.Data;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using Autofac;
using Autofac.Features.Metadata;
using Dapper;
using Microsoft.Extensions.Hosting;
using HashStormCore.Configuration;
using HashStormCore.Extensions;
using HashStormCore.Messaging;
using HashStormCore.Mining;
using HashStormCore.Notifications.Messages;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;
using NLog;
using Contract = HashStormCore.Contracts.Contract;

namespace HashStormCore.Payments;

/// <summary>
/// Coin agnostic payment processor
/// </summary>
public class PayoutManager : BackgroundService
{
    public PayoutManager(IComponentContext ctx,
        IConnectionFactory cf,
        IBlockRepository blockRepo,
        IShareRepository shareRepo,
        IBalanceRepository balanceRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(messageBus);

        this.ctx = ctx;
        this.cf = cf;
        this.blockRepo = blockRepo;
        this.shareRepo = shareRepo;
        this.balanceRepo = balanceRepo;
        this.messageBus = messageBus;
        this.clusterConfig = clusterConfig;

        interval = TimeSpan.FromSeconds(clusterConfig.PaymentProcessing.Interval > 0 ?
            clusterConfig.PaymentProcessing.Interval : 600);
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IBalanceRepository balanceRepo;
    private readonly IBlockRepository blockRepo;
    private readonly IConnectionFactory cf;
    private readonly IComponentContext ctx;
    private readonly IShareRepository shareRepo;
    private readonly IMessageBus messageBus;
    private readonly TimeSpan interval;
    private readonly ConcurrentDictionary<string, IMiningPool> pools = new();
    private readonly ClusterConfig clusterConfig;
    private readonly CompositeDisposable disposables = new();

#if !DEBUG
    private static readonly TimeSpan initialRunDelay = TimeSpan.FromMinutes(1);
#else
    private static readonly TimeSpan initialRunDelay = TimeSpan.FromSeconds(15);
#endif

    private void AttachPool(IMiningPool pool)
    {
        pools.TryAdd(pool.Config.Id, pool);
    }

    private void OnPoolStatusNotification(PoolStatusNotification notification)
    {
        if(notification.Status == PoolStatus.Online)
            AttachPool(notification.Pool);
    }

    private async Task ProcessPoolsAsync(CancellationToken ct)
    {
        foreach(var pool in pools.Values.ToArray().Where(x => x.Config.Enabled && x.Config.PaymentProcessing.Enabled))
        {
            var poolConfig = pool.Config;
            PoolPayoutLock payoutLock = null;

            logger.Info(() => $"Processing payments for pool {poolConfig.Id}");

            try
            {
                payoutLock = await TryAcquirePoolPayoutLockAsync(poolConfig.Id, ct);
                if(payoutLock == null)
                {
                    logger.Info(() => $"[{poolConfig.Id}] Skipping payment processing because another processor holds the pool payout lock");
                    continue;
                }

                if(!IsPaymentProcessingEnabled(pool))
                {
                    logger.Info(() => $"[{poolConfig.Id}] Skipping payment processing because it was disabled before the locked cycle started");
                    continue;
                }

                var family = HandleFamilyOverride(poolConfig.Template.Family, poolConfig);

                // resolve payout handler
                var handlerImpl = ctx.Resolve<IEnumerable<Meta<Lazy<IPayoutHandler, CoinFamilyAttribute>>>>()
                    .First(x => x.Value.Metadata.SupportedFamilies.Contains(family)).Value;

                var handler = handlerImpl.Value;
                await handler.ConfigureAsync(clusterConfig, poolConfig, ct);

                // resolve payout scheme
                var scheme = ctx.ResolveKeyed<IPayoutScheme>(poolConfig.PaymentProcessing.PayoutScheme);

                await UpdatePoolBalancesAsync(pool, poolConfig, handler, scheme, ct);

                if(!IsPaymentProcessingEnabled(pool))
                {
                    logger.Info(() => $"[{poolConfig.Id}] Skipping balance payouts because payment processing was disabled during block accounting");
                    continue;
                }

                await PayoutPoolBalancesAsync(pool, poolConfig, handler, ct);
            }

            catch(InvalidOperationException ex)
            {
                logger.Error(ex.InnerException ?? ex, () => $"[{poolConfig.Id}] Payment processing failed");
            }

            catch(AggregateException ex)
            {
                switch(ex.InnerException)
                {
                    case HttpRequestException httpEx:
                        logger.Error(() => $"[{poolConfig.Id}] Payment processing failed: {httpEx.Message}");
                        break;

                    default:
                        logger.Error(ex.InnerException, () => $"[{poolConfig.Id}] Payment processing failed");
                        break;
                }
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => $"[{poolConfig.Id}] Payment processing failed");
            }

            finally
            {
                if(payoutLock != null)
                    await ReleasePoolPayoutLockAsync(payoutLock);
            }
        }
    }

    private static bool IsPaymentProcessingEnabled(IMiningPool pool)
    {
        return pool.Config.Enabled && pool.Config.PaymentProcessing?.Enabled == true;
    }

    private async Task<PoolPayoutLock> TryAcquirePoolPayoutLockAsync(string poolId, CancellationToken ct)
    {
        var lockKey = GetPoolPayoutLockKey(poolId);
        var con = await cf.OpenConnectionAsync();

        try
        {
            var acquired = await con.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_lock(@lockKey)",
                new { lockKey },
                cancellationToken: ct));

            if(!acquired)
            {
                con.Dispose();
                return null;
            }

            logger.Debug(() => $"[{poolId}] Acquired payout advisory lock {lockKey}");
            return new PoolPayoutLock(poolId, lockKey, con);
        }

        catch
        {
            con.Dispose();
            throw;
        }
    }

    private async Task ReleasePoolPayoutLockAsync(PoolPayoutLock payoutLock)
    {
        try
        {
            var released = await payoutLock.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_advisory_unlock(@lockKey)",
                new { lockKey = payoutLock.LockKey }));

            if(!released)
                logger.Warn(() => $"[{payoutLock.PoolId}] Payout advisory lock {payoutLock.LockKey} was not held during release");
            else
                logger.Debug(() => $"[{payoutLock.PoolId}] Released payout advisory lock {payoutLock.LockKey}");
        }

        catch(Exception ex)
        {
            logger.Warn(ex, () => $"[{payoutLock.PoolId}] Failed to explicitly release payout advisory lock {payoutLock.LockKey}; closing the connection will release any session lock");
        }

        finally
        {
            payoutLock.Dispose();
        }
    }

    private static long GetPoolPayoutLockKey(string poolId)
    {
        var input = Encoding.UTF8.GetBytes($"HashStormCore:payout:{poolId}");
        var hash = SHA256.HashData(input);
        var value = 0UL;

        for(var i = 0; i < sizeof(ulong); i++)
            value = (value << 8) | hash[i];

        return unchecked((long) value);
    }

    private sealed class PoolPayoutLock : IDisposable
    {
        public PoolPayoutLock(string poolId, long lockKey, IDbConnection connection)
        {
            PoolId = poolId;
            LockKey = lockKey;
            Connection = connection;
        }

        public string PoolId { get; }
        public long LockKey { get; }
        public IDbConnection Connection { get; }

        public void Dispose()
        {
            Connection.Dispose();
        }
    }

    private static CoinFamily HandleFamilyOverride(CoinFamily family, PoolConfig pool)
    {
        switch(family)
        {
            case CoinFamily.Equihash:
                var equihashTemplate = pool.Template.As<EquihashCoinTemplate>();

                if(equihashTemplate.UseBitcoinPayoutHandler)
                    return CoinFamily.Bitcoin;

                break;
            
            case CoinFamily.Progpow:
            case CoinFamily.Satoshicash:
                return CoinFamily.Bitcoin;
        }

        return family;
    }

    private async Task UpdatePoolBalancesAsync(IMiningPool pool, PoolConfig poolConfig, IPayoutHandler handler, IPayoutScheme scheme, CancellationToken ct)
    {
        // get pending blockRepo for pool
        var pendingBlocks = await cf.Run(con => blockRepo.GetPendingBlocksForPoolAsync(con, poolConfig.Id));

        // classify
        var updatedBlocks = await handler.ClassifyBlocksAsync(pool, pendingBlocks, ct);

        if(updatedBlocks.Any())
        {
            foreach(var block in updatedBlocks.OrderBy(x => x.Created))
            {
                logger.Info(() => $"Processing payments for pool {poolConfig.Id}, block {block.BlockHeight}");

                await cf.RunTx(async (con, tx) =>
                {
                    if(!block.Effort.HasValue)  // fill block effort if empty
                        await CalculateBlockEffortAsync(pool, poolConfig, block, handler, ct);

                    if(!block.MinerEffort.HasValue)  // fill block miner effort if empty
                        await CalculateMinerEffortAsync(pool, poolConfig, block, handler, ct);

                    switch(block.Status)
                    {
                        case BlockStatus.Confirmed:
                            // blockchains that do not support block-reward payments via coinbase Tx
                            // must generate balance records for all reward recipients instead
                            var blockReward = await handler.UpdateBlockRewardBalancesAsync(con, tx, pool, block, ct);

                            await scheme.UpdateBalancesAsync(con, tx, pool, handler, block, blockReward, ct);
                            await blockRepo.UpdateBlockAsync(con, tx, block);
                            break;

                        case BlockStatus.Orphaned:
                        case BlockStatus.Pending:
                            await blockRepo.UpdateBlockAsync(con, tx, block);
                            break;
                    }
                });
            }
        }

        else
            logger.Info(() => $"No updated blocks for pool {poolConfig.Id}");
    }

    private async Task PayoutPoolBalancesAsync(IMiningPool pool, PoolConfig config, IPayoutHandler handler, CancellationToken ct)
    {
        var poolBalancesOverMinimum = await cf.Run(con =>
            balanceRepo.GetPoolBalancesOverThresholdAsync(con, config.Id, config.PaymentProcessing.MinimumPayment));

        if(poolBalancesOverMinimum.Length > 0)
        {
            try
            {
                await handler.PayoutAsync(pool, poolBalancesOverMinimum, ct);
            }

            catch(Exception ex)
            {
                await NotifyPayoutFailureAsync(poolBalancesOverMinimum, config, ex);
                throw;
            }
        }

        else
            logger.Info(() => $"No balances over configured minimum payout for pool {config.Id}");
    }

    private Task NotifyPayoutFailureAsync(Balance[] balances, PoolConfig pool, Exception ex)
    {
        messageBus.SendMessage(new PaymentNotification(pool.Id, ex.Message, balances.Sum(x => x.Amount), pool.Template.Symbol));

        return Task.CompletedTask;
    }

    private async Task CalculateBlockEffortAsync(IMiningPool pool, PoolConfig poolConfig, Block block, IPayoutHandler handler, CancellationToken ct)
    {
        // get share date-range
        var from = DateTime.MinValue;
        var to = block.Created;

        // get last block for pool
        var lastBlock = await cf.Run(con => blockRepo.GetBlockBeforeAsync(con, poolConfig.Id, new[]
        {
            BlockStatus.Confirmed,
            BlockStatus.Orphaned,
            BlockStatus.Pending,
        }, block.Created));

        if(lastBlock != null)
            from = lastBlock.Created;

        block.Effort = await cf.Run(con =>
            shareRepo.GetEffectiveAccumulatedShareDifficultyBetweenAsync(con, pool.Config.Id, from, to, ct));

        if(block.Effort.HasValue)
            block.Effort = handler.AdjustBlockEffort(block.Effort.Value);
    }

    private async Task CalculateMinerEffortAsync(IMiningPool pool, PoolConfig poolConfig, Block block, IPayoutHandler handler, CancellationToken ct)
    {
        // get share date-range
        var from = DateTime.MinValue;
        var to = block.Created;

	var miner = block.Miner;

        // get last block for pool even for "MinerEffort". We use the same method as pool effort because adding miner address in the equation will just create an overlap in the final calculation
        var lastBlock = await cf.Run(con => blockRepo.GetBlockBeforeAsync(con, poolConfig.Id, new[]
        {
            BlockStatus.Confirmed,
            BlockStatus.Orphaned,
            BlockStatus.Pending,
        }, block.Created));

        if(lastBlock != null)
            from = lastBlock.Created;

	block.MinerEffort = await cf.Run(con => shareRepo.GetMinerShareDifficultyBetweenAsync(con, pool.Config.Id, miner, from, to, ct));

        if(block.MinerEffort.HasValue)
            block.MinerEffort = handler.AdjustBlockEffort(block.MinerEffort.Value);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // monitor pool lifetime
            disposables.Add(messageBus.Listen<PoolStatusNotification>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(OnPoolStatusNotification));

            logger.Info(() => "Online");

            // Allow all pools to actually come up before the first payment processing run
            await Task.Delay(initialRunDelay, ct);

            using var timer = new PeriodicTimer(interval);

            do
            {
                try
                {
                    await ProcessPoolsAsync(ct);
                }

                catch(OperationCanceledException)
                {
                    // ignored
                }

                catch(Exception ex)
                {
                    logger.Error(ex);
                }
            } while(await timer.WaitForNextTickAsync(ct));

            logger.Info(() => "Offline");
        }

        finally
        {
            disposables.Dispose();
        }
    }
}
