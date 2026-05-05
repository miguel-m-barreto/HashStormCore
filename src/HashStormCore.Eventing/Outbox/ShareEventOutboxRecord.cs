using HashStormCore.Contracts.Eventing;

namespace HashStormCore.Eventing.Outbox;

public class ShareEventOutboxRecord
{
    public string SegmentName { get; set; } = string.Empty;
    public long Offset { get; set; }
    public long NextOffset { get; set; }
    public ShareEvent Event { get; set; } = new();
}
