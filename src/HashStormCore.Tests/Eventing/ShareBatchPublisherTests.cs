using System;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Abstractions;
using HashStormCore.Eventing.Publishing;
using HashStormCore.Eventing.Queue;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HashStormCore.Tests.Eventing;

public class ShareBatchPublisherTests
{
    [Fact]
    public async Task FlushesByMaxEvents()
    {
        var queue = new InMemoryShareEventQueue();
        await queue.EnqueueAsync(Event("1", ShareEventType.ShareRejected), CancellationToken.None);
        await queue.EnqueueAsync(Event("2", ShareEventType.ShareStale), CancellationToken.None);

        var publisher = CreatePublisher(queue, new ShareBatchPublisherOptions { MaxEvents = 2, MaxDelayMs = 10_000 });
        var batch = await publisher.ReadNextBatchAsync(CancellationToken.None);

        Assert.Equal(2, batch.EventCount);
    }

    [Fact]
    public async Task FlushesByMaxApproxBytes()
    {
        var queue = new InMemoryShareEventQueue();
        await queue.EnqueueAsync(Event(new string('a', 512), ShareEventType.ShareRejected), CancellationToken.None);

        var publisher = CreatePublisher(queue, new ShareBatchPublisherOptions { MaxEvents = 10, MaxApproxBytes = 100, MaxDelayMs = 10_000 });
        var batch = await publisher.ReadNextBatchAsync(CancellationToken.None);

        Assert.Single(batch.Events);
        Assert.True(batch.ApproxBytes >= 100);
    }

    [Fact]
    public async Task FlushesByMaxDelay()
    {
        var queue = new InMemoryShareEventQueue();
        await queue.EnqueueAsync(Event("1", ShareEventType.ShareRejected), CancellationToken.None);

        var publisher = CreatePublisher(queue, new ShareBatchPublisherOptions { MaxEvents = 10, MaxDelayMs = 50 });
        var batch = await publisher.ReadNextBatchAsync(CancellationToken.None);

        Assert.Single(batch.Events);
    }

    [Fact]
    public async Task CriticalEventFlushesImmediately()
    {
        var queue = new InMemoryShareEventQueue();
        await queue.EnqueueAsync(Event("critical", ShareEventType.BlockCandidate), CancellationToken.None);
        await queue.EnqueueAsync(Event("later", ShareEventType.ShareRejected), CancellationToken.None);

        var publisher = CreatePublisher(queue, new ShareBatchPublisherOptions { MaxEvents = 10, MaxDelayMs = 10_000 });
        var batch = await publisher.ReadNextBatchAsync(CancellationToken.None);

        Assert.Single(batch.Events);
        Assert.Equal(ShareEventType.BlockCandidate, batch.Events[0].EventType);
    }

    [Fact]
    public async Task ShareAcceptedFlushesCriticalBatchImmediately()
    {
        var queue = new InMemoryShareEventQueue();
        await queue.EnqueueAsync(Event("accepted", ShareEventType.ShareAccepted), CancellationToken.None);
        await queue.EnqueueAsync(Event("later", ShareEventType.ShareRejected), CancellationToken.None);

        var publisher = CreatePublisher(queue, new ShareBatchPublisherOptions { MaxEvents = 10, MaxDelayMs = 10_000 });
        var batch = await publisher.ReadNextBatchAsync(CancellationToken.None);

        Assert.Single(batch.Events);
        Assert.Equal(ShareEventType.ShareAccepted, batch.Events[0].EventType);
    }

    [Fact]
    public async Task NewTemplateDoesNotFlushCriticalBatch()
    {
        var queue = new InMemoryShareEventQueue();
        await queue.EnqueueAsync(Event("template", ShareEventType.NewTemplate), CancellationToken.None);
        await queue.EnqueueAsync(Event("telemetry", ShareEventType.ShareRejected), CancellationToken.None);

        var publisher = CreatePublisher(queue, new ShareBatchPublisherOptions { MaxEvents = 2, MaxDelayMs = 10_000 });
        var batch = await publisher.ReadNextBatchAsync(CancellationToken.None);

        Assert.Equal(2, batch.EventCount);
        Assert.Equal(ShareEventType.NewTemplate, batch.Events[0].EventType);
        Assert.Equal(ShareEventType.ShareRejected, batch.Events[1].EventType);
    }

    [Fact]
    public async Task QueueDoesNotDropUnderPressure()
    {
        var queue = new InMemoryShareEventQueue(new ShareEventHandoffOptions { SoftMaxBufferedEvents = 1 });

        await queue.EnqueueAsync(Event("1"), CancellationToken.None);
        await queue.EnqueueAsync(Event("2"), CancellationToken.None);

        Assert.Equal(2, queue.Count);
        Assert.True(queue.IsAboveSoftThreshold);
    }

    private static ShareBatchPublisher CreatePublisher(IShareEventQueue queue, ShareBatchPublisherOptions options)
    {
        return new ShareBatchPublisher(queue, new FakeTransport(), options, NullLogger<ShareBatchPublisher>.Instance);
    }

    private static ShareEvent Event(string miner, ShareEventType type = ShareEventType.ShareAccepted)
    {
        return new ShareEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = type,
            PoolId = "pool",
            Miner = miner,
            Created = DateTime.UtcNow,
            Difficulty = 1
        };
    }

    private class FakeTransport : IShareEventBatchTransport
    {
        public Task<ShareBatchPublishResult> PublishAsync(ShareEventBatch batch, CancellationToken ct)
        {
            return Task.FromResult(ShareBatchPublishResult.Success);
        }
    }
}
