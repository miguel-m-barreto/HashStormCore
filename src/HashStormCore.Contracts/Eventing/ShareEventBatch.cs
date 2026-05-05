namespace HashStormCore.Contracts.Eventing;

public class ShareEventBatch
{
    public string BatchId { get; set; } = string.Empty;
    public string ProducerId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string ClusterName { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public long Sequence { get; set; }
    public int EventCount { get; set; }
    public int ApproxBytes { get; set; }
    public IReadOnlyList<ShareEvent> Events { get; set; } = Array.Empty<ShareEvent>();
    public ShareEventBatchMetadata Metadata { get; set; } = new();
}
