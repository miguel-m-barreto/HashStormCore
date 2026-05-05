namespace HashStormCore.LiveAggregator.Configuration;

public class LiveAggregatorConfig
{
    public string RedisConnectionString { get; set; } = "localhost:6379,abortConnect=false";
    public string StreamName { get; set; } = "hashstorm:share-events";
    public string ConsumerName { get; set; } = Environment.MachineName;
    public int ReadBatchSize { get; set; } = 16;
    public int LiveWindowSeconds { get; set; } = 600;
    public int RedisTtlSeconds { get; set; } = 900;
    public int BucketSeconds { get; set; } = 10;
    public int PendingMinIdleMs { get; set; } = 60000;
    public string[] Pools { get; set; } = Array.Empty<string>();
}
