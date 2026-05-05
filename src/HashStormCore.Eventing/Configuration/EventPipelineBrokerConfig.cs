namespace HashStormCore.Eventing.Configuration;

public class EventPipelineBrokerConfig
{
    public string Type { get; set; } = "redis-streams";
    public string ConnectionString { get; set; } = "localhost:6379,abortConnect=false";
    public string StreamName { get; set; } = "hashstorm:share-events";
    public string ProducerId { get; set; } = Environment.MachineName;
    public bool StartupRequired { get; set; }
    public int PublishRetryDelayMs { get; set; } = 250;
    public int PublishMaxRetryDelayMs { get; set; } = 5000;
    public int PendingMinIdleMs { get; set; } = 60000;
}
