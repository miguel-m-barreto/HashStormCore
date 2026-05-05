using System;
using System.Linq;
using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Mapping;
using Xunit;

namespace HashStormCore.Tests.Eventing;

public class ShareEventMapperTests
{
    [Fact]
    public void MapsAcceptedShareFieldsCorrectly()
    {
        var created = DateTime.UtcNow;

        var result = ShareEventMapper.Map(new ShareEventSource
        {
            PoolId = "btc",
            CoinSymbol = "BTC",
            CoinFamily = "Bitcoin",
            Miner = "miner1",
            Worker = "rig1",
            Created = created,
            Difficulty = 42,
            NetworkDifficulty = 1000,
            ShareMultiplier = 2,
            IpAddress = "127.0.0.1",
            UserAgent = "test"
        });

        Assert.Equal(ShareEventType.ShareAccepted, result.EventType);
        Assert.Equal("btc", result.PoolId);
        Assert.Equal("miner1", result.Miner);
        Assert.Equal("rig1", result.Worker);
        Assert.Equal(42, result.Difficulty);
        Assert.False(string.IsNullOrWhiteSpace(result.EventId));
    }

    [Fact]
    public void DoesNotIncludeSecretOrRawPayloadFields()
    {
        var properties = typeof(ShareEvent).GetProperties().Select(x => x.Name).ToArray();

        Assert.DoesNotContain(properties, x => x.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, x => x.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, x => x.Contains("Raw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, x => x.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GeneratesUniqueEventIdForIdenticalLookingEvents()
    {
        var source = new ShareEventSource
        {
            PoolId = "pool",
            Miner = "miner",
            Worker = "worker",
            Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Difficulty = 1
        };

        var first = ShareEventMapper.Map(source);
        var second = ShareEventMapper.Map(source);

        Assert.NotEqual(first.EventId, second.EventId);
    }
}
