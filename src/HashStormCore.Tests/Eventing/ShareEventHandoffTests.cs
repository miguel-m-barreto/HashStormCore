using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Queue;
using Xunit;

namespace HashStormCore.Tests.Eventing;

public class ShareEventHandoffTests
{
    [Fact]
    public async Task DefaultHandoffIsNoDrop()
    {
        var queue = new InMemoryShareEventQueue(new ShareEventHandoffOptions { SoftMaxBufferedEvents = 1 });

        await queue.EnqueueAsync(Event("1"), CancellationToken.None);
        await queue.EnqueueAsync(Event("2"), CancellationToken.None);

        Assert.Equal(2, queue.Count);
        Assert.True(queue.IsAboveSoftThreshold);
        Assert.False(queue.IsAboveCriticalThreshold);
    }

    [Fact]
    public async Task DefaultQueueIsUnboundedNoDrop()
    {
        var queue = new InMemoryShareEventQueue(new ShareEventHandoffOptions
        {
            SoftMaxBufferedEvents = 1,
            CriticalBufferedEvents = 2
        });

        await queue.EnqueueAsync(Event("1"), CancellationToken.None);
        await queue.EnqueueAsync(Event("2"), CancellationToken.None);
        await queue.EnqueueAsync(Event("3"), CancellationToken.None);

        Assert.Equal(3, queue.Count);
        Assert.True(queue.IsAboveSoftThreshold);
        Assert.True(queue.IsAboveCriticalThreshold);
    }

    [Fact]
    public void ProductionOptionsExposeOnlyThresholds()
    {
        var names = typeof(ShareEventHandoffOptions)
            .GetProperties()
            .Select(x => x.Name)
            .ToArray();

        Assert.DoesNotContain("MaxBufferedEvents", names);
        Assert.DoesNotContain("MaxBufferedBytes", names);
        Assert.DoesNotContain("OverflowPolicy", names);
        Assert.Contains("SoftMaxBufferedEvents", names);
        Assert.Contains("CriticalBufferedEvents", names);
    }

    [Fact]
    public async Task CriticalThresholdIsAlarmOnly()
    {
        var queue = new InMemoryShareEventQueue(new ShareEventHandoffOptions
        {
            SoftMaxBufferedEvents = 1,
            CriticalBufferedEvents = 2
        });

        await queue.EnqueueAsync(Event("1"), CancellationToken.None);
        await queue.EnqueueAsync(Event("2"), CancellationToken.None);

        Assert.Equal(2, queue.Count);
        Assert.True(queue.IsAboveCriticalThreshold);
        Assert.Equal("1", (await queue.DequeueAsync(CancellationToken.None)).Miner);
        Assert.Equal("2", (await queue.DequeueAsync(CancellationToken.None)).Miner);
    }

    private static ShareEvent Event(string miner) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        EventType = ShareEventType.ShareAccepted,
        PoolId = "pool",
        Miner = miner,
        Created = DateTime.UtcNow
    };
}
