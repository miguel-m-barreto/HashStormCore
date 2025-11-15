using System.Globalization;
using Autofac;
using HashStormCore.Blockchain.Bitcoin;
using HashStormCore.Blockchain.Bitcoin.DaemonResponses;
using HashStormCore.Blockchain.Satoshicash.Configuration;
using HashStormCore.Blockchain.Satoshicash.DaemonResponses;
using HashStormCore.Configuration;
using HashStormCore.Contracts;
using HashStormCore.Crypto;
using HashStormCore.Extensions;
using HashStormCore.JsonRpc;
using HashStormCore.Messaging;
using HashStormCore.Native;
using HashStormCore.Notifications.Messages;
using HashStormCore.Rpc;
using HashStormCore.Stratum;
using HashStormCore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace HashStormCore.Blockchain.Satoshicash;

public class SatoshicashJobManager : BitcoinJobManagerBase<SatoshicashJob>
{
    public SatoshicashJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus,
        IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    private RandomX.randomx_flags? randomXSCashFlagsOverride;
    private RandomX.randomx_flags? randomXSCashFlagsAdd;
    private string currentSeedHash;
    private string randomXSCashRealm;
    private int randomXSCashVmCount;
    private BitcoinTemplate coin;

    protected async Task<RpcResponse<BlockTemplate>> GetBlockTemplateAsync(CancellationToken ct)
    {
        var result = await rpc.ExecuteAsync<BlockTemplate>(logger,
            BitcoinCommands.GetBlockTemplate, ct, extraPoolConfig?.GBTArgs ?? (object) GetBlockTemplateParams());

        return result;
    }

    protected RpcResponse<BlockTemplate> GetBlockTemplateFromJson(string json)
    {
        var result = JsonConvert.DeserializeObject<JsonRpcResponse>(json);

        return new RpcResponse<BlockTemplate>(result!.ResultAs<BlockTemplate>());
    }

    private static SatoshicashJob CreateJob()
    {
        return new SatoshicashJob();
    }

    protected override void PostChainIdentifyConfigure()
    {
        base.PostChainIdentifyConfigure();

        if(poolConfig.EnableInternalStratum == false || coin.HeaderHasherValue is not IHashAlgorithmInit hashInit)
            return;

        if(!hashInit.DigestInit(poolConfig))
            logger.Error(() => $"{hashInit.GetType().Name} initialization failed");
    }

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        if(poolConfig.EnableInternalStratum == true)
        {
            // make sure we have a current seed
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            do
            {
                var blockTemplate = await GetBlockTemplateAsync(ct);

                if(blockTemplate?.Response != null)
                {
                    UpdateHashParams(blockTemplate.Response);
                    break;
                }

                logger.Info(() => "Waiting for first valid block template");
            } while(await timer.WaitForNextTickAsync(ct));
        }
        await base.PostStartInitAsync(ct);
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null, string json = null)
    {
        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

            var response = string.IsNullOrEmpty(json) ?
                await GetBlockTemplateAsync(ct) :
                GetBlockTemplateFromJson(json);

            // may happen if daemon is currently not connected to peers
            if(response.Error != null)
            {
                logger.Warn(() => $"Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                return (false, forceUpdate);
            }

            var blockTemplate = response.Response;
            var job = currentJob;

            var isNew = job == null ||
                (blockTemplate != null &&
                    (job.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
                        blockTemplate.Height > job.BlockTemplate?.Height));

            if(isNew)
                messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

            if(isNew || forceUpdate)
            {
                UpdateHashParams(blockTemplate);

                job = CreateJob();

                job.Init(blockTemplate, NextJobId(),
                    poolConfig, extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,
                    ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue,
                    !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ?? coin.BlockHasherValue,
                    randomXSCashRealm);

                if(isNew)
                {
                    if(via != null)
                        logger.Info(() => $"Detected new block {blockTemplate.Height} [{via}]");
                    else
                        logger.Info(() => $"Detected new block {blockTemplate.Height}");

                    // update stats
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = blockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }

                else
                {
                    if(via != null)
                        logger.Debug(() => $"Template update {blockTemplate?.Height} [{via}]");
                    else
                        logger.Debug(() => $"Template update {blockTemplate?.Height}");
                }

                currentJob = job;
            }

            return (isNew, forceUpdate);
        }

        catch(OperationCanceledException)
        {
            // ignored
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
        }

        return (false, forceUpdate);
    }

    private void UpdateHashParams(BlockTemplate blockTemplate)
    {
        Contract.Requires<ArgumentException>(coin.HasRandomXSCash);

        var randomXSCashParameters = blockTemplate.Extra.SafeExtensionDataAs<RandomXSCashExtra>();
        var blockTemplateSeedHash = SatoshicashUtils.GenerateEpochSeedHash(randomXSCashParameters.EpochDurationRandomXSCash, blockTemplate.CurTime, coin.HeaderHasherValue);

        // detect seed hash change
        if(currentSeedHash != blockTemplateSeedHash)
        {
            logger.Info(()=> $"Detected new seed hash {blockTemplateSeedHash} starting @ height {blockTemplate.Height}");
            if(poolConfig.EnableInternalStratum == true)
            {
                RandomXSCash.WithLock(() =>
                {
                    // delete old seed
                    if(currentSeedHash != null)
                        RandomXSCash.DeleteSeed(randomXSCashRealm, currentSeedHash);
                    // activate new one
                    currentSeedHash = blockTemplateSeedHash;
                    RandomXSCash.CreateSeed(randomXSCashRealm, currentSeedHash, randomXSCashFlagsOverride, randomXSCashFlagsAdd, randomXSCashVmCount);
                });
            }
            else
                currentSeedHash = blockTemplateSeedHash;
        }
    }

    protected override object GetJobParamsForStratum(bool isNew)
    {
        var job = currentJob;
        return job?.GetJobParams(isNew);
    }

    public override SatoshicashJob GetJobForStratum()
    {
        var job = currentJob;
        return job;
    }

    #region API-Surface

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();

        if(pc.EnableInternalStratum == true)
        {
            var satoshicashExtraPoolConfig = pc.Extra.SafeExtensionDataAs<SatoshicashPoolConfigExtra>();

            randomXSCashRealm = !string.IsNullOrEmpty(satoshicashExtraPoolConfig.RandomXRealm) ? satoshicashExtraPoolConfig.RandomXRealm : pc.Id;
            randomXSCashFlagsOverride = MakeRandomXSCashFlags(satoshicashExtraPoolConfig.RandomXFlagsOverride);
            randomXSCashFlagsAdd = MakeRandomXSCashFlags(satoshicashExtraPoolConfig.RandomXFlagsAdd);
            randomXSCashVmCount = satoshicashExtraPoolConfig.RandomXVmCount;
        }

        base.Configure(pc, cc);
    }

    public object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);

        var context = worker.ContextAs<SatoshicashWorkerContext>();

        // assign unique ExtraNonce1 to worker (miner)
        context.ExtraNonce1 = extraNonceProvider.Next();

        // setup response data
        var responseData = new object[]
        {
            context.ExtraNonce1,
            BitcoinConstants.ExtranoncePlaceHolderLength - ExtranonceBytes,
        };

        return responseData;
    }

    public virtual async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission,
        CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if(submission is not object[] submitParams)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<SatoshicashWorkerContext>();

        // extract params
        var workerValue = (submitParams[0] as string)?.Trim();
        var jobId = submitParams[1] as string;
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;

        if(string.IsNullOrEmpty(workerValue))
            throw new StratumException(StratumError.Other, "missing or invalid workername");

        SatoshicashJob job;

        lock(context)
        {
            job = context.GetJob(jobId);
        }

        if(job == null)
            throw new StratumException(StratumError.JobNotFound, "job not found");

        // validate & process
        var (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, nonce);

        // enrich share with common data
        share.PoolId = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint.Address.ToString();
        share.Miner = context.Miner;
        share.Worker = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source = clusterConfig.ClusterName;
        share.Created = clock.Now;

        // if block candidate, submit & check if accepted by network
        if(share.IsBlockCandidate)
        {
            logger.Info(() => $"Submitting block {share.BlockHeight} [{share.BlockHash}]");

            var acceptResponse = await SubmitBlockAsync(share, blockHex, ct);

            // is it still a block candidate?
            share.IsBlockCandidate = acceptResponse.Accepted;

            if(share.IsBlockCandidate)
            {
                logger.Info(() => $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {context.Miner}");

                OnBlockFound();

                // persist the coinbase transaction-hash to allow the payment processor
                // to verify later on that the pool has received the reward for the block
                share.TransactionConfirmationData = acceptResponse.CoinbaseTx;
            }

            else
            {
                // clear fields that no longer apply
                share.TransactionConfirmationData = null;
            }
        }

        return share;
    }

    public double ShareMultiplier => coin.ShareMultiplier;

    #endregion // API-Surface

    private RandomX.randomx_flags? MakeRandomXSCashFlags(JToken token)
    {
        if(token == null)
            return null;

        if(token.Type == JTokenType.Integer)
            return (RandomX.randomx_flags) token.Value<ulong>();
        else if(token.Type == JTokenType.String)
        {
            RandomX.randomx_flags result = 0;
            var value = token.Value<string>();

            foreach(var flag in value.Split("|").Select(x=> x.Trim()).Where(x=> !string.IsNullOrEmpty(x)))
            {
                if(Enum.TryParse(typeof(RandomX.randomx_flags), flag, true, out var flagVal))
                    result |= (RandomX.randomx_flags) flagVal;
            }

            return result;
        }

        return null;
    }
}
