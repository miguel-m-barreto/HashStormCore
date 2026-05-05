using HashStormCore.Contracts.Redis;
using Xunit;

namespace HashStormCore.Tests.Contracts;

public class RedisLiveKeyNamesTests
{
    [Fact]
    public void KeyFormatIsStable()
    {
        Assert.Equal("hashstorm:live:pool1:summary", RedisLiveKeyNames.Summary("pool1"));
        Assert.Equal("hashstorm:live:pool1:miner:miner1:summary", RedisLiveKeyNames.MinerSummary("pool1", "miner1"));
        Assert.Equal("hashstorm:live:pool1:worker:miner1:worker1:summary", RedisLiveKeyNames.WorkerSummary("pool1", "miner1", "worker1"));
    }

    [Fact]
    public void UnsafeNamesAreEncoded()
    {
        Assert.Equal("miner_bad", RedisLiveKeyNames.Encode("miner:bad"));
        Assert.Equal("_", RedisLiveKeyNames.Encode(""));
    }
}
