using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace HashStormCore.Eventing.Publishing;

public class ShareBatchPublisher : BackgroundService
{
    public ShareBatchPublisher(
        IShareEventQueue queue,
        IShareEventBatchTransport transport,
        ShareBatchPublisherOptions options,
        ILogger<ShareBatchPublisher> logger)
    {
        this.queue = queue;
        this.transport = transport;
        this.options = options;
        this.logger = logger;
    }

    private readonly IShareEventQueue queue;
    private readonly IShareEventBatchTransport transport;
    private readonly ShareBatchPublisherOptions options;
    private readonly ILogger<ShareBatchPublisher> logger;
    private long sequence;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Share batch publisher online");

        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await ReadNextBatchAsync(stoppingToken);
                if(batch.Events.Count > 0)
                    await PublishWithRetryAsync(batch, stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch(Exception ex)
            {
                logger.LogError(ex, "Share batch publisher loop failed");
                await Task.Delay(GetRetryDelay(0), stoppingToken);
            }
        }

        logger.LogInformation("Share batch publisher offline");
    }

    public async Task<ShareEventBatch> ReadNextBatchAsync(CancellationToken ct)
    {
        var events = new List<ShareEvent>(Math.Max(1, options.MaxEvents));
        var approxBytes = 0;
        var first = await queue.DequeueAsync(ct);
        Add(first);

        if(IsCritical(first))
            return CreateBatch(events, approxBytes);

        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timerCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, options.MaxDelayMs)));

        while(events.Count < options.MaxEvents && approxBytes < options.MaxApproxBytes)
        {
            ShareEvent next;

            try
            {
                next = await queue.DequeueAsync(timerCts.Token);
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested)
            {
                break;
            }

            Add(next);

            if(IsCritical(next))
                break;
        }

        return CreateBatch(events, approxBytes);

        void Add(ShareEvent shareEvent)
        {
            events.Add(shareEvent);
            approxBytes += EstimateSize(shareEvent);
        }
    }

    private async Task PublishWithRetryAsync(ShareEventBatch batch, CancellationToken ct)
    {
        var attempt = 0;

        while(!ct.IsCancellationRequested)
        {
            try
            {
                var result = await transport.PublishAsync(batch, ct);
                if(result?.Published != true)
                    throw new InvalidOperationException($"Transport did not publish share event batch {batch.BatchId}");

                return;
            }
            catch(Exception ex)
            {
                attempt++;
                var delay = GetRetryDelay(attempt);
                logger.LogError(ex, "Failed to publish share event batch {BatchId}; retrying in {Delay} ms", batch.BatchId, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    private TimeSpan GetRetryDelay(int attempt)
    {
        var min = Math.Max(1, options.PublishRetryDelayMs);
        var max = Math.Max(min, options.PublishMaxRetryDelayMs);
        var delay = Math.Min(max, min * Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromMilliseconds(delay);
    }

    private ShareEventBatch CreateBatch(IReadOnlyList<ShareEvent> events, int approxBytes)
    {
        return new ShareEventBatch
        {
            BatchId = Guid.NewGuid().ToString("N"),
            ProducerId = options.ProducerId,
            NodeId = options.NodeId,
            ClusterName = options.ClusterName,
            Created = DateTime.UtcNow,
            Sequence = Interlocked.Increment(ref sequence),
            EventCount = events.Count,
            ApproxBytes = approxBytes,
            Events = events,
            Metadata = new ShareEventBatchMetadata
            {
                BrokerType = options.BrokerType,
                StreamName = options.StreamName
            }
        };
    }

    private static bool IsCritical(ShareEvent shareEvent)
    {
        return shareEvent.EventType is ShareEventType.ShareAccepted or
            ShareEventType.BlockCandidate or
            ShareEventType.BlockAccepted or
            ShareEventType.BlockRejected;
    }

    private static int EstimateSize(ShareEvent shareEvent)
    {
        return Math.Max(128, JsonConvert.SerializeObject(shareEvent).Length);
    }
}
