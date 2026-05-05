namespace HashStormCore.Contracts.Eventing;

public class ShareEventBatchMetadata
{
    public string StreamName { get; set; } = string.Empty;
    public string BrokerType { get; set; } = string.Empty;
}
