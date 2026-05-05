using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Abstractions;
using HashStormCore.Eventing.Outbox;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HashStormCore.Tests.Eventing;

public class ShareEventOutboxWriterTests
{
    [Fact]
    public async Task ShutdownFlushesDequeuedBatchWithNonCanceledToken()
    {
        var queue = new ControlledQueue(Event("dequeued"));
        var outbox = new RecordingOutbox();
        var logger = new RecordingLogger<ShareEventOutboxWriter>();
        var writer = new ShareEventOutboxWriter(queue, outbox, Options(), logger);

        await writer.StartAsync(CancellationToken.None);
        await queue.FirstDequeued.WaitAsync(TimeSpan.FromSeconds(5));
        await writer.StopAsync(CancellationToken.None);

        var append = Assert.Single(outbox.Appends);
        Assert.Equal("dequeued", Assert.Single(append.Events).Miner);
        Assert.False(append.TokenWasCanceled);
    }

    [Fact]
    public async Task ShutdownDrainsQueuedEventsBeforeExit()
    {
        var queue = new ControlledQueue(null, Event("queued-1"), Event("queued-2"));
        var outbox = new RecordingOutbox();
        var logger = new RecordingLogger<ShareEventOutboxWriter>();
        var writer = new ShareEventOutboxWriter(queue, outbox, Options(), logger);

        await writer.StartAsync(CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);

        var drained = outbox.Appends.SelectMany(x => x.Events).Select(x => x.Miner).ToArray();
        Assert.Equal(new[] { "queued-1", "queued-2" }, drained);
        Assert.All(outbox.Appends, x => Assert.False(x.TokenWasCanceled));
    }

    [Fact]
    public async Task ShutdownDrainFailureLogsCritical()
    {
        var queue = new ControlledQueue(null, Event("queued"));
        var outbox = new RecordingOutbox { ThrowOnAppend = true };
        var logger = new RecordingLogger<ShareEventOutboxWriter>();
        var writer = new ShareEventOutboxWriter(queue, outbox, Options(), logger);

        await writer.StartAsync(CancellationToken.None);
        await Record.ExceptionAsync(() => writer.StopAsync(CancellationToken.None));

        Assert.Contains(logger.Entries, x => x.Level == LogLevel.Critical &&
            x.Message.Contains("Failed to drain", StringComparison.Ordinal));
    }

    private static ShareEventOutboxOptions Options() => new()
    {
        WriterFlushEvents = 16,
        WriterFlushBytes = 1024 * 1024,
        WriterFlushMs = 10_000,
        FsyncMode = "periodic",
        FsyncIntervalMs = 10_000
    };

    private static ShareEvent Event(string miner) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        EventType = ShareEventType.ShareAccepted,
        PoolId = "pool",
        Miner = miner,
        Created = DateTime.UtcNow,
        Difficulty = 1
    };

    private sealed class ControlledQueue : IShareEventQueue
    {
        public ControlledQueue(ShareEvent firstDequeueEvent, params ShareEvent[] remainingEvents)
        {
            this.firstDequeueEvent = firstDequeueEvent;
            remaining = new Queue<ShareEvent>(remainingEvents);
        }

        private readonly ShareEvent firstDequeueEvent;
        private readonly Queue<ShareEvent> remaining;
        private int dequeueCount;
        private readonly TaskCompletionSource firstDequeued = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstDequeued => firstDequeued.Task;
        public int Count => remaining.Count;
        public long ApproximateBytes => Count * 128L;
        public bool IsAboveSoftThreshold => false;
        public bool IsAboveCriticalThreshold => false;

        public ValueTask EnqueueAsync(ShareEvent shareEvent, CancellationToken ct)
        {
            remaining.Enqueue(shareEvent);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ShareEvent> DequeueAsync(CancellationToken ct)
        {
            if(Interlocked.Increment(ref dequeueCount) == 1 && firstDequeueEvent != null)
            {
                firstDequeued.SetResult();
                return ValueTask.FromResult(firstDequeueEvent);
            }

            return new ValueTask<ShareEvent>(WaitForCancellationAsync(ct));
        }

        public bool TryDequeue(out ShareEvent shareEvent)
        {
            if(remaining.Count > 0)
            {
                shareEvent = remaining.Dequeue();
                return true;
            }

            shareEvent = null;
            return false;
        }

        private static async Task<ShareEvent> WaitForCancellationAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unexpected queue wake-up");
        }
    }

    private sealed class RecordingOutbox : IShareEventOutbox
    {
        public List<AppendCall> Appends { get; } = new();
        public bool ThrowOnAppend { get; set; }

        public Task AppendAsync(IReadOnlyList<ShareEvent> events, bool flushToDisk, CancellationToken ct)
        {
            Appends.Add(new AppendCall(events.ToArray(), flushToDisk, ct.IsCancellationRequested));

            if(ThrowOnAppend)
                throw new InvalidOperationException("append failed");

            return Task.CompletedTask;
        }

        public Task FlushAsync(bool flushToDisk, CancellationToken ct) => Task.CompletedTask;
        public async IAsyncEnumerable<ShareEventOutboxRecord> ReadFromCheckpointAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task AdvanceCheckpointAsync(ShareEventOutboxRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record AppendCall(IReadOnlyList<ShareEvent> Events, bool FlushToDisk, bool TokenWasCanceled);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
