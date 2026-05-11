using HashStormCore.Contracts.Eventing;
using HashStormCore.Contracts.Live;
using HashStormCore.Contracts.Redis;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace HashStormCore.LiveAggregator.Services;

public class RedisLiveStateWriter : IAsyncDisposable
{
    public RedisLiveStateWriter(string connectionString, int ttlSeconds, int windowSeconds, int bucketSeconds)
    {
        this.ttlSeconds = Math.Max(1, ttlSeconds);
        this.windowSeconds = Math.Max(1, windowSeconds);
        this.bucketSeconds = Math.Max(1, bucketSeconds);
        multiplexer = ConnectionMultiplexer.Connect(connectionString);
        db = multiplexer.GetDatabase();
    }

    private readonly int ttlSeconds;
    private readonly int windowSeconds;
    private readonly int bucketSeconds;
    private readonly ConnectionMultiplexer multiplexer;
    private readonly IDatabase db;
    private readonly LiveBucketCalculator bucketCalculator = new();

    public async Task WriteBatchAsync(IEnumerable<ShareEvent> events, CancellationToken ct)
    {
        var batch = events.ToArray();
        foreach(var shareEvent in batch)
        {
            ct.ThrowIfCancellationRequested();
            await WriteEventAsync(shareEvent);
        }

        foreach(var poolId in batch.Select(x => x.PoolId).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            await RebuildPoolWindowAsync(poolId);
    }

    public async Task ClearPoolStateAsync(string poolId)
    {
        if(string.IsNullOrWhiteSpace(poolId))
            return;

        await DeleteKeysByPatternAsync(RedisLiveKeyNames.PoolPrefix(poolId) + "*");
    }

    private async Task DeleteKeysByPatternAsync(string pattern)
    {
        var endpoints = multiplexer.GetEndPoints();
        var keys = new HashSet<RedisKey>();

        foreach(var endpoint in endpoints)
        {
            var server = multiplexer.GetServer(endpoint);
            if(!server.IsConnected)
                continue;

            foreach(var key in server.Keys(database: db.Database, pattern: pattern))
                keys.Add(key);
        }

        if(keys.Count > 0)
            await db.KeyDeleteAsync(keys.ToArray());
    }

    public Task MarkStatusAsync(string poolId, string status, int windowSeconds, int availableWindowSeconds)
    {
        var dto = new LivePoolStatusDto
        {
            PoolId = poolId,
            Status = status,
            WindowSeconds = windowSeconds,
            AvailableWindowSeconds = availableWindowSeconds,
            Updated = DateTime.UtcNow
        };

        return db.StringSetAsync(RedisLiveKeyNames.Status(poolId), JsonConvert.SerializeObject(dto), TimeSpan.FromSeconds(ttlSeconds));
    }

    private async Task WriteEventAsync(ShareEvent shareEvent)
    {
        if(string.IsNullOrWhiteSpace(shareEvent.PoolId))
            return;

        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        var bucket = BucketStart(shareEvent.Created);
        if(!await MarkSeenAsync(shareEvent, bucket, ttl))
            return;

        var poolBucket = RedisLiveKeyNames.PoolBucket(shareEvent.PoolId, bucket);
        var miner = shareEvent.Miner ?? string.Empty;
        var worker = shareEvent.Worker ?? string.Empty;

        await IncrementBucketAsync(poolBucket, shareEvent);
        await db.KeyExpireAsync(poolBucket, ttl);

        if(!string.IsNullOrWhiteSpace(miner))
        {
            await db.SetAddAsync(RedisLiveKeyNames.BucketMiners(shareEvent.PoolId, bucket), miner);
            await db.KeyExpireAsync(RedisLiveKeyNames.BucketMiners(shareEvent.PoolId, bucket), ttl);
            await IncrementBucketAsync(RedisLiveKeyNames.MinerBucket(shareEvent.PoolId, miner, bucket), shareEvent);
            await db.KeyExpireAsync(RedisLiveKeyNames.MinerBucket(shareEvent.PoolId, miner, bucket), ttl);
        }

        if(!string.IsNullOrWhiteSpace(miner) && !string.IsNullOrWhiteSpace(worker))
        {
            await db.SortedSetAddAsync(RedisLiveKeyNames.WorkersLastSeen(shareEvent.PoolId), $"{miner}.{worker}", new DateTimeOffset(shareEvent.Created.ToUniversalTime()).ToUnixTimeSeconds());
            await db.SetAddAsync(RedisLiveKeyNames.BucketWorkers(shareEvent.PoolId, bucket), $"{miner}\t{worker}");
            await db.KeyExpireAsync(RedisLiveKeyNames.BucketWorkers(shareEvent.PoolId, bucket), ttl);
            await IncrementBucketAsync(RedisLiveKeyNames.WorkerBucket(shareEvent.PoolId, miner, worker, bucket), shareEvent);
            await db.KeyExpireAsync(RedisLiveKeyNames.WorkerBucket(shareEvent.PoolId, miner, worker, bucket), ttl);
        }
    }

    private async Task RebuildPoolWindowAsync(string poolId)
    {
        var now = DateTime.UtcNow;
        var from = now.AddSeconds(-windowSeconds);
        var buckets = Buckets(from, now).ToArray();
        var pool = new Counter();
        var miners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workerPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long? oldestNonEmptyBucket = null;

        foreach(var bucket in buckets)
        {
            var bucketCounter = await ReadCounterAsync(RedisLiveKeyNames.PoolBucket(poolId, bucket));
            pool.Add(bucketCounter);
            if(bucketCounter.Total > 0 && oldestNonEmptyBucket == null)
                oldestNonEmptyBucket = bucket;

            foreach(var miner in await db.SetMembersAsync(RedisLiveKeyNames.BucketMiners(poolId, bucket)))
                miners.Add(miner.ToString());
            foreach(var worker in await db.SetMembersAsync(RedisLiveKeyNames.BucketWorkers(poolId, bucket)))
                workerPairs.Add(worker.ToString());
        }

        await db.SortedSetRemoveRangeByScoreAsync(RedisLiveKeyNames.WorkersLastSeen(poolId), double.NegativeInfinity, new DateTimeOffset(from).ToUnixTimeSeconds() - 1);

        var available = oldestNonEmptyBucket == null
            ? 0
            : Math.Min(windowSeconds, Math.Max(0, (int)(now - DateTimeOffset.FromUnixTimeSeconds(oldestNonEmptyBucket.Value).UtcDateTime).TotalSeconds));
        var effectiveWindowSeconds = Math.Max(1, available >= windowSeconds ? windowSeconds : available);
        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        await DeleteKeysByPatternAsync(RedisLiveKeyNames.PoolPrefix(poolId) + "miner:*:workers:active");
        var summary = new LivePoolSummaryDto
        {
            PoolId = poolId,
            Difficulty = pool.Difficulty,
            Hashrate = pool.Difficulty / effectiveWindowSeconds,
            Accepted = pool.Accepted,
            Rejected = pool.Rejected,
            Stale = pool.Stale,
            MinersOnline = miners.Count,
            WorkersOnline = workerPairs.Count,
            Updated = now
        };

        await db.StringSetAsync(RedisLiveKeyNames.Summary(poolId), JsonConvert.SerializeObject(summary), ttl);
        await MarkStatusAsync(poolId, available >= windowSeconds ? "ready" : "warming_up", windowSeconds, available);
        await RebuildMinersAsync(poolId, miners, workerPairs, buckets, ttl, effectiveWindowSeconds);
    }

    private async Task RebuildMinersAsync(string poolId, IEnumerable<string> miners, IEnumerable<string> workerPairs, IReadOnlyList<long> buckets, TimeSpan ttl, int effectiveWindowSeconds)
    {
        await db.KeyDeleteAsync(RedisLiveKeyNames.TopMiners(poolId));
        var workersByMiner = workerPairs
            .Select(ParseWorkerPair)
            .Where(x => !string.IsNullOrWhiteSpace(x.Miner) && !string.IsNullOrWhiteSpace(x.Worker))
            .GroupBy(x => x.Miner, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Worker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach(var miner in miners.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var counter = new Counter();
            foreach(var bucket in buckets)
                counter.Add(await ReadCounterAsync(RedisLiveKeyNames.MinerBucket(poolId, miner, bucket)));

            workersByMiner.TryGetValue(miner, out var workers);
            workers ??= Array.Empty<string>();
            var latestWorkerSeen = await LatestWorkerSeenAsync(poolId, miner, workers);
            var minerSummary = new LiveMinerSummaryDto
            {
                PoolId = poolId,
                Miner = miner,
                Difficulty = counter.Difficulty,
                Hashrate = counter.Difficulty / effectiveWindowSeconds,
                Accepted = counter.Accepted,
                Rejected = counter.Rejected,
                Stale = counter.Stale,
                WorkersOnline = workers.Length,
                LastSeen = latestWorkerSeen ?? DateTime.UnixEpoch
            };

            await db.StringSetAsync(RedisLiveKeyNames.MinerSummary(poolId, miner), JsonConvert.SerializeObject(minerSummary), ttl);
            await db.SortedSetAddAsync(RedisLiveKeyNames.TopMiners(poolId), miner, minerSummary.Difficulty);
            await db.KeyExpireAsync(RedisLiveKeyNames.TopMiners(poolId), ttl);
            await db.KeyDeleteAsync(RedisLiveKeyNames.ActiveMinerWorkers(poolId, miner));

            foreach(var worker in workers)
            {
                await db.SetAddAsync(RedisLiveKeyNames.ActiveMinerWorkers(poolId, miner), worker);
                await db.KeyExpireAsync(RedisLiveKeyNames.ActiveMinerWorkers(poolId, miner), ttl);
                await RebuildWorkerAsync(poolId, miner, worker, buckets, ttl, effectiveWindowSeconds);
            }
        }
    }

    private async Task RebuildWorkerAsync(string poolId, string miner, string worker, IReadOnlyList<long> buckets, TimeSpan ttl, int effectiveWindowSeconds)
    {
        var counter = new Counter();
        foreach(var bucket in buckets)
            counter.Add(await ReadCounterAsync(RedisLiveKeyNames.WorkerBucket(poolId, miner, worker, bucket)));

        if(counter.Total == 0)
            return;

        var lastSeen = await db.SortedSetScoreAsync(RedisLiveKeyNames.WorkersLastSeen(poolId), $"{miner}.{worker}");
        var dto = new LiveWorkerSummaryDto
        {
            PoolId = poolId,
            Miner = miner,
            Worker = worker,
            Difficulty = counter.Difficulty,
            Hashrate = counter.Difficulty / effectiveWindowSeconds,
            Accepted = counter.Accepted,
            Rejected = counter.Rejected,
            Stale = counter.Stale,
            LastSeen = lastSeen.HasValue ? DateTimeOffset.FromUnixTimeSeconds((long)lastSeen.Value).UtcDateTime : DateTime.UnixEpoch
        };

        await db.StringSetAsync(RedisLiveKeyNames.WorkerSummary(poolId, miner, worker), JsonConvert.SerializeObject(dto), ttl);
    }

    private async Task<bool> MarkSeenAsync(ShareEvent shareEvent, long bucket, TimeSpan ttl)
    {
        if(string.IsNullOrWhiteSpace(shareEvent.EventId))
            return true;

        var key = RedisLiveKeyNames.SeenEvents(shareEvent.PoolId, bucket);
        var added = await db.SetAddAsync(key, shareEvent.EventId);
        await db.KeyExpireAsync(key, ttl);
        return added;
    }

    private async Task<DateTime?> LatestWorkerSeenAsync(string poolId, string miner, IEnumerable<string> workers)
    {
        DateTime? result = null;
        foreach(var worker in workers)
        {
            var score = await db.SortedSetScoreAsync(RedisLiveKeyNames.WorkersLastSeen(poolId), $"{miner}.{worker}");
            if(!score.HasValue)
                continue;

            var seen = DateTimeOffset.FromUnixTimeSeconds((long)score.Value).UtcDateTime;
            if(result == null || seen > result.Value)
                result = seen;
        }

        return result;
    }

    private static (string Miner, string Worker) ParseWorkerPair(string value)
    {
        var parts = (value ?? string.Empty).Split('\t', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (string.Empty, string.Empty);
    }

    private async Task IncrementBucketAsync(string key, ShareEvent shareEvent)
    {
        var entries = new List<HashEntry>
        {
            new("total", 1)
        };

        switch(shareEvent.EventType)
        {
            case ShareEventType.ShareAccepted:
            case ShareEventType.BlockCandidate:
            case ShareEventType.BlockAccepted:
                entries.Add(new HashEntry("accepted", 1));
                entries.Add(new HashEntry("difficulty", bucketCalculator.WeightedDifficulty(shareEvent)));
                break;

            case ShareEventType.ShareStale:
                entries.Add(new HashEntry("stale", 1));
                break;

            case ShareEventType.ShareRejected:
            case ShareEventType.BlockRejected:
                entries.Add(new HashEntry("rejected", 1));
                break;
        }

        foreach(var entry in entries)
            await db.HashIncrementAsync(key, entry.Name, (double) entry.Value);
    }

    private async Task<Counter> ReadCounterAsync(string key)
    {
        var entries = await db.HashGetAllAsync(key);
        var result = new Counter();

        foreach(var entry in entries)
        {
            var value = double.TryParse(entry.Value.ToString(), out var d) ? d : 0;
            switch(entry.Name.ToString())
            {
                case "total":
                    result.Total += (long)value;
                    break;
                case "accepted":
                    result.Accepted += (long)value;
                    break;
                case "rejected":
                    result.Rejected += (long)value;
                    break;
                case "stale":
                    result.Stale += (long)value;
                    break;
                case "difficulty":
                    result.Difficulty += value;
                    break;
            }
        }

        return result;
    }

    private long BucketStart(DateTime created)
    {
        var seconds = new DateTimeOffset(created.ToUniversalTime()).ToUnixTimeSeconds();
        return seconds - seconds % bucketSeconds;
    }

    private IEnumerable<long> Buckets(DateTime from, DateTime to)
    {
        var start = BucketStart(from);
        var end = BucketStart(to);
        for(var bucket = start; bucket <= end; bucket += bucketSeconds)
            yield return bucket;
    }

    public async ValueTask DisposeAsync()
    {
        await multiplexer.CloseAsync();
        await multiplexer.DisposeAsync();
    }

    private class Counter
    {
        public long Total { get; set; }
        public long Accepted { get; set; }
        public long Rejected { get; set; }
        public long Stale { get; set; }
        public double Difficulty { get; set; }

        public void Add(Counter other)
        {
            Total += other.Total;
            Accepted += other.Accepted;
            Rejected += other.Rejected;
            Stale += other.Stale;
            Difficulty += other.Difficulty;
        }
    }
}
