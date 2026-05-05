using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Abstractions;
using HashStormCore.Eventing.Publishing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace HashStormCore.Eventing.Outbox;

public class ShareEventOutboxPublisher : BackgroundService
{
    public ShareEventOutboxPublisher(IShareEventOutbox outbox, IShareEventBatchTransport transport, ShareBatchPublisherOptions publisherOptions,
        ShareEventOutboxOptions outboxOptions, ILogger<ShareEventOutboxPublisher> logger)
    {
        this.outbox = outbox;
        this.transport = transport;
        this.publisherOptions = publisherOptions;
        this.outboxOptions = outboxOptions;
        this.logger = logger;
    }

    private readonly IShareEventOutbox outbox;
    private readonly IShareEventBatchTransport transport;
    private readonly ShareBatchPublisherOptions publisherOptions;
    private readonly ShareEventOutboxOptions outboxOptions;
    private readonly ILogger<ShareEventOutboxPublisher> logger;
    private long sequence;
    private DateTime lastBacklogLog = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Share event outbox publisher online");

        while(!stoppingToken.IsCancellationRequested)
        {
            LogBacklogIfNeeded();
            var publishedAny = false;
            var records = new List<ShareEventOutboxRecord>(Math.Max(1, outboxOptions.PublisherMaxEvents));
            var bytes = 0;

            await foreach(var record in outbox.ReadFromCheckpointAsync(stoppingToken))
            {
                records.Add(record);
                bytes += Math.Max(128, JsonConvert.SerializeObject(record.Event).Length);
                if(records.Count >= outboxOptions.PublisherMaxEvents || bytes >= outboxOptions.PublisherMaxApproxBytes)
                    break;
            }

            if(records.Count == 0)
            {
                await Task.Delay(250, stoppingToken);
                continue;
            }

            var batch = CreateBatch(records.Select(x => x.Event).ToArray(), bytes);
            await PublishWithRetryAsync(batch, stoppingToken);
            await outbox.AdvanceCheckpointAsync(records[^1], stoppingToken);
            publishedAny = true;

            if(!publishedAny)
                await Task.Delay(250, stoppingToken);
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
                    throw new InvalidOperationException($"Transport did not publish outbox batch {batch.BatchId}");

                return;
            }
            catch(Exception ex)
            {
                attempt++;
                var delay = GetRetryDelay(attempt);
                logger.LogCritical(ex, "Failed to publish outbox batch {BatchId}; WAL backlog will grow; retrying in {Delay} ms",
                    batch.BatchId, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    private ShareEventBatch CreateBatch(IReadOnlyList<ShareEvent> events, int approxBytes)
    {
        return new ShareEventBatch
        {
            BatchId = Guid.NewGuid().ToString("N"),
            ProducerId = publisherOptions.ProducerId,
            NodeId = publisherOptions.NodeId,
            ClusterName = publisherOptions.ClusterName,
            Created = DateTime.UtcNow,
            Sequence = Interlocked.Increment(ref sequence),
            EventCount = events.Count,
            ApproxBytes = approxBytes,
            Events = events,
            Metadata = new ShareEventBatchMetadata
            {
                BrokerType = publisherOptions.BrokerType,
                StreamName = publisherOptions.StreamName
            }
        };
    }

    private TimeSpan GetRetryDelay(int attempt)
    {
        var min = Math.Max(1, outboxOptions.PublishRetryDelayMs);
        var max = Math.Max(min, outboxOptions.PublishMaxRetryDelayMs);
        var delay = Math.Min(max, min * Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromMilliseconds(delay);
    }

    private void LogBacklogIfNeeded()
    {
        if(DateTime.UtcNow - lastBacklogLog < TimeSpan.FromSeconds(30))
            return;

        var bytes = Directory.Exists(outboxOptions.Directory)
            ? new DirectoryInfo(outboxOptions.Directory).EnumerateFiles("*.wal").Sum(x => x.Length)
            : 0;

        if(bytes < outboxOptions.SoftBacklogBytes)
            return;

        lastBacklogLog = DateTime.UtcNow;

        if(bytes >= outboxOptions.CriticalBacklogBytes)
            logger.LogCritical("Share event WAL backlog is above critical threshold: {Bytes} bytes. No unpublished records are deleted or dropped; check Redis/downstream availability and disk capacity.", bytes);
        else
            logger.LogWarning("Share event WAL backlog is above soft threshold: {Bytes} bytes. No unpublished records are deleted or dropped; publisher continues retrying.", bytes);
    }
}
