using System.Text;

namespace HashStormCore.Contracts.Redis;

public static class RedisLiveKeyNames
{
    public static string PoolPrefix(string poolId) => $"hashstorm:live:{Encode(poolId)}:";
    public static string Status(string poolId) => $"hashstorm:live:{Encode(poolId)}:status";
    public static string Summary(string poolId) => $"hashstorm:live:{Encode(poolId)}:summary";
    public static string TopMiners(string poolId) => $"hashstorm:live:{Encode(poolId)}:miners:top";
    public static string WorkersLastSeen(string poolId) => $"hashstorm:live:{Encode(poolId)}:workers:lastseen";
    public static string MinerWorkers(string poolId, string miner) => $"hashstorm:live:{Encode(poolId)}:miner:{Encode(miner)}:workers";
    public static string ActiveMinerWorkers(string poolId, string miner) => $"hashstorm:live:{Encode(poolId)}:miner:{Encode(miner)}:workers:active";
    public static string MinerSummary(string poolId, string miner) => $"hashstorm:live:{Encode(poolId)}:miner:{Encode(miner)}:summary";
    public static string WorkerSummary(string poolId, string miner, string worker) => $"hashstorm:live:{Encode(poolId)}:worker:{Encode(miner)}:{Encode(worker)}:summary";
    public static string PoolBucket(string poolId, long timestamp) => $"hashstorm:live:{Encode(poolId)}:bucket:{timestamp}:summary";
    public static string BucketMiners(string poolId, long timestamp) => $"hashstorm:live:{Encode(poolId)}:bucket:{timestamp}:miners";
    public static string BucketWorkers(string poolId, long timestamp) => $"hashstorm:live:{Encode(poolId)}:bucket:{timestamp}:workers";
    public static string SeenEvents(string poolId, long timestamp) => $"hashstorm:live:{Encode(poolId)}:seen:{timestamp}";
    public static string MinerBucket(string poolId, string miner, long timestamp) => $"hashstorm:live:{Encode(poolId)}:miner:{Encode(miner)}:bucket:{timestamp}";
    public static string WorkerBucket(string poolId, string miner, string worker, long timestamp) => $"hashstorm:live:{Encode(poolId)}:worker:{Encode(miner)}:{Encode(worker)}:bucket:{timestamp}";

    public static string Encode(string value)
    {
        if(string.IsNullOrWhiteSpace(value))
            return "_";

        var sb = new StringBuilder(value.Length);

        foreach(var ch in value.Trim())
        {
            if(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')
                sb.Append(ch);
            else
                sb.Append('_');
        }

        return sb.Length == 0 ? "_" : sb.ToString();
    }
}
