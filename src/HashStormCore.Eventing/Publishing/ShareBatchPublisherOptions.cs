namespace HashStormCore.Eventing.Publishing;

public class ShareBatchPublisherOptions
{
    public string ProducerId { get; set; } = Environment.MachineName;
    public string NodeId { get; set; } = Environment.MachineName;
    public string ClusterName { get; set; } = string.Empty;
    public string StreamName { get; set; } = "hashstorm:share-events";
    public string BrokerType { get; set; } = "redis-streams";
    public int MaxEvents { get; set; } = 256;
    public int MaxApproxBytes { get; set; } = 524_288;
    public int MaxDelayMs { get; set; } = 1000;
    public int PublishRetryDelayMs { get; set; } = 250;
    public int PublishMaxRetryDelayMs { get; set; } = 5000;
}
