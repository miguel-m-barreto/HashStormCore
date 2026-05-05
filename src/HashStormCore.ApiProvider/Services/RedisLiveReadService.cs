using HashStormCore.Contracts.Live;
using HashStormCore.Contracts.Redis;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace HashStormCore.ApiProvider.Services;

public class RedisLiveReadService : IAsyncDisposable
{
    public RedisLiveReadService(string connectionString)
    {
        multiplexer = ConnectionMultiplexer.Connect(connectionString);
        db = multiplexer.GetDatabase();
    }

    private readonly ConnectionMultiplexer multiplexer;
    private readonly IDatabase db;

    public Task<LivePoolSummaryDto> GetPoolSummaryAsync(string poolId) =>
        GetJsonAsync<LivePoolSummaryDto>(RedisLiveKeyNames.Summary(poolId));

    public Task<LivePoolStatusDto> GetPoolStatusAsync(string poolId) =>
        GetJsonAsync<LivePoolStatusDto>(RedisLiveKeyNames.Status(poolId));

    public Task<LiveMinerSummaryDto> GetMinerSummaryAsync(string poolId, string miner) =>
        GetJsonAsync<LiveMinerSummaryDto>(RedisLiveKeyNames.MinerSummary(poolId, miner));

    public async Task<IReadOnlyList<LiveTopMinerDto>> GetTopMinersAsync(string poolId, int count)
    {
        var entries = await db.SortedSetRangeByRankWithScoresAsync(
            RedisLiveKeyNames.TopMiners(poolId), 0, Math.Max(0, count - 1), Order.Descending);

        var result = new List<LiveTopMinerDto>();
        foreach(var entry in entries)
        {
            var summary = await GetMinerSummaryAsync(poolId, entry.Element.ToString());
            result.Add(new LiveTopMinerDto
            {
                Miner = entry.Element.ToString(),
                Difficulty = entry.Score,
                Hashrate = summary?.Hashrate ?? entry.Score,
                Accepted = summary?.Accepted ?? 0,
                LastSeen = summary?.LastSeen ?? DateTime.UtcNow
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<LiveWorkerSummaryDto>> GetMinerWorkersAsync(string poolId, string miner)
    {
        var workers = await db.SetMembersAsync(RedisLiveKeyNames.ActiveMinerWorkers(poolId, miner));
        var result = new List<LiveWorkerSummaryDto>();

        foreach(var worker in workers.Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var summary = await GetJsonAsync<LiveWorkerSummaryDto>(RedisLiveKeyNames.WorkerSummary(poolId, miner, worker));
            if(summary != null)
                result.Add(summary);
        }

        return result;
    }

    private async Task<T> GetJsonAsync<T>(string key) where T : class
    {
        var value = await db.StringGetAsync(key);
        return value.HasValue ? JsonConvert.DeserializeObject<T>(value.ToString()) : null;
    }

    public async ValueTask DisposeAsync()
    {
        await multiplexer.CloseAsync();
        multiplexer.Dispose();
    }
}
