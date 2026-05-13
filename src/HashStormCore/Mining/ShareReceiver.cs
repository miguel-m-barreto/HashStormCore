using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using Microsoft.Extensions.Hosting;
using HashStormCore.Blockchain;
using HashStormCore.Configuration;
using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Abstractions;
using HashStormCore.Eventing.Mapping;
using HashStormCore.Contracts;
using HashStormCore.Extensions;
using HashStormCore.Messaging;
using HashStormCore.Notifications.Messages;
using HashStormCore.Time;
using HashStormCore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using ProtoBuf;
using ZeroMQ;

namespace HashStormCore.Mining;

/// <summary>
/// Receives external shares from relays and re-publishes for consumption
/// </summary>
public class ShareReceiver : BackgroundService
{
    public ShareReceiver(
        ClusterConfig clusterConfig,
        IMasterClock clock,
        IMessageBus messageBus,
        IShareEventQueue shareEventQueue = null)
    {
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);

        this.clusterConfig = clusterConfig;
        this.clock = clock;
        this.messageBus = messageBus;
        this.shareEventQueue = shareEventQueue;
        queue = new BlockingCollection<RelayShareMessage>(Math.Max(1, clusterConfig.ShareReceiver?.MaxQueueSize ?? 10000));
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IMasterClock clock;
    private readonly IMessageBus messageBus;
    private readonly IShareEventQueue shareEventQueue;
    private readonly ClusterConfig clusterConfig;
    private readonly CompositeDisposable disposables = new();
    private readonly ConcurrentDictionary<string, PoolContext> pools = new();
    private readonly BlockingCollection<RelayShareMessage> queue;

    readonly JsonSerializer serializer = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private class PoolContext
    {
        public PoolContext(IMiningPool pool, ILogger logger)
        {
            Pool = pool;
            Logger = logger;
        }

        public IMiningPool Pool { get; }
        public ILogger Logger { get; }
        public DateTime? LastBlock { get; set; }
        public long BlockHeight { get; set; }
    }

    private sealed record RelayShareMessage(string Url, string Topic, uint Flags, byte[] Data);

    private void AttachPool(IMiningPool pool)
    {
        var ctx = new PoolContext(pool, LogUtil.GetPoolScopedLogger(typeof(ShareRecorder), pool.Config));
        pools.TryAdd(pool.Config.Id, ctx);
    }

    private void OnPoolStatusNotification(PoolStatusNotification notification)
    {
        if(notification.Status == PoolStatus.Online)
            AttachPool(notification.Pool);
    }

    private Task StartMessageReceiver(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            Thread.CurrentThread.Name = "ShareReceiver Socket Poller";
            var timeout = TimeSpan.FromMilliseconds(5000);
            var reconnectTimeout = TimeSpan.FromSeconds(60);

            var relays = clusterConfig.ShareRelays
                .DistinctBy(x => $"{x.Url}:{x.CurveServerPublicKey}:{x.CurveClientSecretKey}")
                .ToArray();

            while(!ct.IsCancellationRequested)
            {
                // track last message received per endpoint
                var lastMessageReceived = relays.Select(_ => clock.Now).ToArray();

                try
                {
                    // setup sockets
                    var sockets = relays.Select(x=> SetupSubSocket(x)).ToArray();

                    using(new CompositeDisposable(sockets))
                    {
                        var pollItems = sockets.Select(_ => ZPollItem.CreateReceiver()).ToArray();

                        while(!ct.IsCancellationRequested)
                        {
                            if(sockets.PollIn(pollItems, out var messages, out var error, timeout))
                            {
                                for(var i = 0; i < messages.Length; i++)
                                {
                                    var msg = messages[i];

                                    if(msg != null)
                                    {
                                        lastMessageReceived[i] = clock.Now;

                                        RelayShareMessage relayMessage;
                                        using(msg)
                                        {
                                            // ZMessage/ZFrame instances are owned by the ZMQ receiver thread.
                                            // Copy managed frame data before handing work to async processors.
                                            if(!TryCreateRelayShareMessage(relays[i].Url, msg, out relayMessage))
                                                continue;
                                        }

                                        queue.Add(relayMessage, ct);
                                    }

                                    else if(clock.Now - lastMessageReceived[i] > reconnectTimeout)
                                    {
                                        // re-create socket
                                        sockets[i].Dispose();
                                        sockets[i] = SetupSubSocket(relays[i], true);

                                        // reset clock
                                        lastMessageReceived[i] = clock.Now;

                                        logger.Info(() => $"Receive timeout of {reconnectTimeout.TotalSeconds} seconds exceeded. Re-connecting to {relays[i].Url} ...");
                                    }
                                }

                                if(error != null)
                                    logger.Error(() => $"{nameof(ShareReceiver)}: {error.Name} [{error.Name}] during receive");
                            }

                            else
                            {
                                // check for timeouts
                                for(var i = 0; i < messages.Length; i++)
                                {
                                    if(clock.Now - lastMessageReceived[i] > reconnectTimeout)
                                    {
                                        // re-create socket
                                        sockets[i].Dispose();
                                        sockets[i] = SetupSubSocket(relays[i], true);

                                        // reset clock
                                        lastMessageReceived[i] = clock.Now;

                                        logger.Info(() => $"Receive timeout of {reconnectTimeout.TotalSeconds} seconds exceeded. Re-connecting to {relays[i].Url} ...");
                                    }
                                }
                            }
                        }
                    }
                }

                catch(Exception ex)
                {
                    logger.Error(() => $"{nameof(ShareReceiver)}: {ex}");

                    if(!ct.IsCancellationRequested)
                        Thread.Sleep(5000);
                }
            }
        }, ct);
    }

    private static ZSocket SetupSubSocket(ShareRelayEndpointConfig relay, bool silent = false)
    {
        var subSocket = new ZSocket(ZSocketType.SUB);
        subSocket.SetupCurveTlsClient(relay.CurveServerPublicKey, relay.CurveClientSecretKey, logger);
        subSocket.Connect(relay.Url);
        subSocket.SubscribeAll();

        if(!silent)
        {
            if(subSocket.CurveServerKey != null)
                logger.Info($"Monitoring external stratum {relay.Url} using key {subSocket.CurveServerKey.ToHexString()}");
            else
                logger.Info($"Monitoring external stratum {relay.Url}");
        }

        return subSocket;
    }

    private static bool TryCreateRelayShareMessage(string url, ZMessage msg, out RelayShareMessage relayMessage)
    {
        relayMessage = null;

        try
        {
            var topic = msg[0].ToString(Encoding.UTF8);
            var flags = msg[1].ReadUInt32();
            var data = msg[2].Read()?.ToArray();
            relayMessage = new RelayShareMessage(url, topic, flags, data);
            return true;
        }

        catch(Exception ex)
        {
            logger.Warn(ex, $"Malformed relay message from {url}. Ignoring ...");
            return false;
        }
    }

    private Task StartMessageProcessors(CancellationToken ct)
    {
        // Preserve the previous effective single-processor behavior; pool stats updates are not synchronized.
        return Task.Run(() => ProcessMessages(ct), ct);
    }

    private async Task ProcessMessages(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var msg = queue.Take(ct);
                await ProcessMessage(msg);
            }

            catch(Exception ex)
            {
                logger.Error(ex);
            }
        }
    }

    private async Task ProcessMessage(RelayShareMessage msg)
    {
        // validate
        if(string.IsNullOrEmpty(msg.Topic) || !pools.TryGetValue(msg.Topic, out var poolContext))
        {
            logger.Warn(() => $"Received share for pool '{msg.Topic}' which is not known locally. Ignoring ...");
            return;
        }

        if(msg.Data?.Length == 0)
        {
            logger.Warn(() => $"Received empty data from {msg.Url}/{msg.Topic}. Ignoring ...");
            return;
        }

        // TMP FIX
        var flags = msg.Flags;
        if((flags & ShareRelay.WireFormatMask) == 0)
            flags = BitConverter.ToUInt32(BitConverter.GetBytes(flags).ToNewReverseArray());

        // deserialize
        var wireFormat = (ShareRelay.WireFormat) (flags & ShareRelay.WireFormatMask);

        Share share = null;

        switch(wireFormat)
        {
            case ShareRelay.WireFormat.Json:
                using(var stream = new MemoryStream(msg.Data))
                {
                    using(var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        using(var jreader = new JsonTextReader(reader))
                        {
                            share = serializer.Deserialize<Share>(jreader);
                        }
                    }
                }

                break;

            case ShareRelay.WireFormat.ProtocolBuffers:
                using(var stream = new MemoryStream(msg.Data))
                {
                    share = Serializer.Deserialize<Share>(stream);
                    share.BlockReward = (decimal) share.BlockRewardDouble;
                }

                break;

            default:
                logger.Error(() => $"Unsupported wire format {wireFormat} of share received from {msg.Url}/{msg.Topic} ");
                break;
        }

        if(share == null)
        {
            logger.Error(() => $"Unable to deserialize share received from {msg.Url}/{msg.Topic}");
            return;
        }

        // store
        share.PoolId = msg.Topic;
        share.Created = clock.Now;

        if(clusterConfig.EventPipeline?.Enabled == true)
            await EnqueueExternalShareEvent(poolContext, share);
        else
            messageBus.SendMessage(share);

        // update poolstats from shares
        if(poolContext != null)
        {
            var pool = poolContext.Pool;
            var shareMultiplier = poolContext.Pool.ShareMultiplier;

            poolContext.Logger.Debug(() => $"External {(!string.IsNullOrEmpty(share.Source) ? $"[{share.Source.ToUpper()}] " : string.Empty)}share accepted: D={Math.Round(share.Difficulty * shareMultiplier, 4)}");

            messageBus.SendTelemetry(share.PoolId, TelemetryCategory.Share, TimeSpan.Zero, true);

            if(pool.NetworkStats != null)
            {
                pool.NetworkStats.BlockHeight = (ulong) share.BlockHeight;
                pool.NetworkStats.NetworkDifficulty = share.NetworkDifficulty;

                if(poolContext.BlockHeight != share.BlockHeight)
                {
                    pool.NetworkStats.LastNetworkBlockTime = clock.Now;
                    poolContext.BlockHeight = share.BlockHeight;
                    poolContext.LastBlock = clock.Now;
                }

                else
                    pool.NetworkStats.LastNetworkBlockTime = poolContext.LastBlock;
            }
        }

        else
            logger.Debug(() => $"External {(!string.IsNullOrEmpty(share.Source) ? $"[{share.Source.ToUpper()}] " : string.Empty)}share accepted: D={Math.Round(share.Difficulty, 4)}");
    }

    private async Task EnqueueExternalShareEvent(PoolContext poolContext, Share share)
    {
        var pool = poolContext?.Pool;
        var shareEvent = ShareEventMapper.Map(new ShareEventSource
        {
            PoolId = share.PoolId,
            CoinSymbol = pool?.Config?.Template?.Symbol ?? string.Empty,
            CoinFamily = pool?.Config?.Template?.Family.ToString() ?? string.Empty,
            Miner = share.Miner,
            Worker = share.Worker,
            Source = share.Source,
            Created = share.Created,
            BlockHeight = share.BlockHeight > 0 ? share.BlockHeight : null,
            Difficulty = share.Difficulty,
            NetworkDifficulty = share.NetworkDifficulty,
            ShareMultiplier = pool?.ShareMultiplier ?? 1d,
            IsBlockCandidate = share.IsBlockCandidate,
            BlockHash = share.BlockHash,
            IpAddress = share.IpAddress,
            UserAgent = share.UserAgent,
            TransactionConfirmationData = share.TransactionConfirmationData,
            BlockReward = share.BlockReward,
            BlockType = share.BlockType
        }, ShareEventType.ShareAccepted);

        if(shareEventQueue != null)
            await shareEventQueue.EnqueueAsync(shareEvent, CancellationToken.None);
        else
            logger.Warn(() => $"Event pipeline is enabled but no share event queue is registered for external relay share from {share.Source}; event_id={shareEvent.EventId}");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(clusterConfig.ShareRelays != null)
        {
            try
            {
                // monitor pool lifetime
                disposables.Add(messageBus.Listen<PoolStatusNotification>()
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(OnPoolStatusNotification));

                // process messages
                await Task.WhenAll(
                    StartMessageReceiver(ct),
                    StartMessageProcessors(ct));
            }

            finally
            {
                disposables.Dispose();
            }
        }
    }
}
