using System;
using HashStormCore.Contracts.Eventing;
using HashStormCore.LiveAggregator.Services;
using Xunit;

namespace HashStormCore.Tests.Live;

public class LiveWindowRebuilderTests
{
    [Fact]
    public void RebuildsFromEventsUsingCreated()
    {
        var now = DateTime.UtcNow;
        var rebuilder = new LiveWindowRebuilder();

        var snapshot = rebuilder.Rebuild(new[]
        {
            Event(now.AddSeconds(-10)),
            Event(now.AddSeconds(-20))
        }, now, 60);

        Assert.Equal(2, snapshot.Events.Count);
        Assert.True(snapshot.AvailableWindowSeconds >= 20);
    }

    [Fact]
    public void IgnoresEventsOlderThanWindow()
    {
        var now = DateTime.UtcNow;
        var rebuilder = new LiveWindowRebuilder();

        var snapshot = rebuilder.Rebuild(new[]
        {
            Event(now.AddSeconds(-10)),
            Event(now.AddSeconds(-120))
        }, now, 60);

        Assert.Single(snapshot.Events);
    }

    [Fact]
    public void MarksWarmingUpWhenInsufficientWindowData()
    {
        var now = DateTime.UtcNow;
        var rebuilder = new LiveWindowRebuilder();

        var snapshot = rebuilder.Rebuild(new[] { Event(now.AddSeconds(-10)) }, now, 60);

        Assert.True(snapshot.IsWarmingUp);
    }

    private static ShareEvent Event(DateTime created)
    {
        return new ShareEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = ShareEventType.ShareAccepted,
            PoolId = "pool",
            Miner = "miner",
            Created = created,
            Difficulty = 1
        };
    }
}
