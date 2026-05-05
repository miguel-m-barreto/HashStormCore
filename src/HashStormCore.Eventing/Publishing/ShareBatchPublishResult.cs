namespace HashStormCore.Eventing.Publishing;

public class ShareBatchPublishResult
{
    public static readonly ShareBatchPublishResult Success = new() { Published = true };

    public bool Published { get; set; }
    public string BrokerMessageId { get; set; } = string.Empty;
}
