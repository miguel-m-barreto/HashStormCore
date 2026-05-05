namespace HashStormCore.DbWriter.Configuration;

public class DbWriterConfig
{
    public string RedisConnectionString { get; set; } = "localhost:6379,abortConnect=false";
    public string StreamName { get; set; } = "hashstorm:share-events";
    public string ConsumerName { get; set; } = Environment.MachineName;
    public int ReadBatchSize { get; set; } = 16;
    public int PendingMinIdleMs { get; set; } = 60000;
    public string PostgresConnectionString { get; set; } = string.Empty;
}
