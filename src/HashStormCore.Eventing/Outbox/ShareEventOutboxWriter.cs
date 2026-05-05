using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace HashStormCore.Eventing.Outbox;

public class ShareEventOutboxWriter : BackgroundService
{
    public ShareEventOutboxWriter(IShareEventQueue queue, IShareEventOutbox outbox, ShareEventOutboxOptions options, ILogger<ShareEventOutboxWriter> logger)
    {
        this.queue = queue;
        this.outbox = outbox;
        this.options = options;
        this.logger = logger;
    }

    private readonly IShareEventQueue queue;
    private readonly IShareEventOutbox outbox;
    private readonly ShareEventOutboxOptions options;
    private readonly ILogger<ShareEventOutboxWriter> logger;
    private readonly SemaphoreSlim shutdownDrainGate = new(1, 1);
    private bool shutdownDrainCompleted;
    private DateTime lastFsync = DateTime.UtcNow;
    private DateTime lastThresholdLog = DateTime.MinValue;
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Share event outbox writer online");

        try
        {
            while(!stoppingToken.IsCancellationRequested)
            {
                var batch = new List<ShareEvent>(Math.Max(1, options.WriterFlushEvents));
                var bytes = 0;
                var flushToDisk = false;

                try
                {
                    var first = await queue.DequeueAsync(stoppingToken);
                    Add(first);
                    LogHandoffPressureIfNeeded();

                    using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timerCts.CancelAfter(TimeSpan.FromMilliseconds(queue.IsAboveSoftThreshold ? 1 : Math.Max(1, options.WriterFlushMs)));
                    var drainMultiplier = queue.IsAboveCriticalThreshold ? 8 : queue.IsAboveSoftThreshold ? 4 : 1;
                    var maxEvents = Math.Max(1, options.WriterFlushEvents * drainMultiplier);
                    var maxBytes = Math.Max(4096, options.WriterFlushBytes * drainMultiplier);

                    while(batch.Count < maxEvents && bytes < maxBytes)
                    {
                        try
                        {
                            Add(await queue.DequeueAsync(timerCts.Token));
                        }
                        catch(OperationCanceledException) when(!stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }
                        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    flushToDisk = ShouldFlushToDisk();
                    await FlushBatchWithShutdownRecoveryAsync(batch, flushToDisk, stoppingToken);
                }
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested && batch.Count > 0)
                {
                    await FlushDequeuedBatchDuringShutdownAsync(batch, flushToDisk, "shutdown cancellation after dequeue");
                    break;
                }
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                void Add(ShareEvent shareEvent)
                {
                    batch.Add(shareEvent);
                    bytes += Math.Max(128, JsonConvert.SerializeObject(shareEvent).Length);
                }
            }
        }
        finally
        {
            if(stoppingToken.IsCancellationRequested)
                await DrainRemainingQueueDuringShutdownOnceAsync();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await DrainRemainingQueueDuringShutdownOnceAsync();
    }

    private bool ShouldFlushToDisk()
    {
        return string.Equals(options.FsyncMode, "always", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(options.FsyncMode, "periodic", StringComparison.OrdinalIgnoreCase) &&
             DateTime.UtcNow - lastFsync >= TimeSpan.FromMilliseconds(Math.Max(1, options.FsyncIntervalMs)));
    }

    private async Task FlushBatchWithShutdownRecoveryAsync(IReadOnlyList<ShareEvent> batch, bool flushToDisk, CancellationToken stoppingToken)
    {
        if(stoppingToken.IsCancellationRequested && batch.Count > 0)
        {
            await FlushDequeuedBatchDuringShutdownAsync(batch, flushToDisk, "shutdown cancellation before WAL append");
            return;
        }

        try
        {
            await FlushBatchAsync(batch, flushToDisk, stoppingToken);
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested && batch.Count > 0)
        {
            await FlushDequeuedBatchDuringShutdownAsync(batch, flushToDisk, "shutdown cancellation during WAL append");
        }
    }

    private async Task FlushDequeuedBatchDuringShutdownAsync(IReadOnlyList<ShareEvent> batch, bool flushToDisk, string reason)
    {
        try
        {
            using var shutdownCts = new CancellationTokenSource(ShutdownFlushTimeout);
            await FlushBatchAsync(batch, flushToDisk, shutdownCts.Token);
        }
        catch(Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to flush {Count} dequeued share events to WAL during shutdown ({Reason}); critical_events={CriticalCount}",
                batch.Count, reason, batch.Count(IsCritical));
            throw;
        }
    }

    private async Task DrainRemainingQueueDuringShutdownOnceAsync()
    {
        await shutdownDrainGate.WaitAsync();
        try
        {
            if(shutdownDrainCompleted)
                return;

            shutdownDrainCompleted = true;
        }
        finally
        {
            shutdownDrainGate.Release();
        }

        await DrainRemainingQueueDuringShutdownAsync();
    }

    private async Task DrainRemainingQueueDuringShutdownAsync()
    {
        var batch = new List<ShareEvent>(Math.Max(1, options.WriterFlushEvents));

        while(queue.TryDequeue(out var shareEvent))
        {
            batch.Add(shareEvent);

            if(batch.Count >= Math.Max(1, options.WriterFlushEvents))
            {
                await FlushDrainBatchDuringShutdownAsync(batch);
                batch.Clear();
            }
        }

        if(batch.Count > 0)
            await FlushDrainBatchDuringShutdownAsync(batch);
    }

    private async Task FlushDrainBatchDuringShutdownAsync(IReadOnlyList<ShareEvent> batch)
    {
        try
        {
            using var shutdownCts = new CancellationTokenSource(ShutdownFlushTimeout);
            await FlushBatchAsync(batch, true, shutdownCts.Token);
        }
        catch(Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to drain {Count} queued share events to WAL during shutdown; critical_events={CriticalCount}",
                batch.Count, batch.Count(IsCritical));
            throw;
        }
    }

    private async Task FlushBatchAsync(IReadOnlyList<ShareEvent> batch, bool flushToDisk, CancellationToken ct)
    {
        if(batch.Count == 0)
            return;

        await outbox.AppendAsync(batch, flushToDisk, ct);
        if(flushToDisk)
            lastFsync = DateTime.UtcNow;
    }

    private static bool IsCritical(ShareEvent shareEvent)
    {
        return shareEvent.EventType is ShareEventType.ShareAccepted or
            ShareEventType.BlockCandidate or
            ShareEventType.BlockAccepted or
            ShareEventType.BlockRejected;
    }

    private void LogHandoffPressureIfNeeded()
    {
        if(!queue.IsAboveSoftThreshold || DateTime.UtcNow - lastThresholdLog < TimeSpan.FromSeconds(30))
            return;

        lastThresholdLog = DateTime.UtcNow;

        if(queue.IsAboveCriticalThreshold)
            logger.LogCritical("Share event handoff queue is above critical threshold: {Count} events, approximately {Bytes} bytes. No events are dropped; WAL writer is draining as fast as configured.",
                queue.Count, queue.ApproximateBytes);
        else
            logger.LogWarning("Share event handoff queue is above soft threshold: {Count} events, approximately {Bytes} bytes. No events are dropped; WAL writer is using accelerated drain settings.",
                queue.Count, queue.ApproximateBytes);
    }
}
