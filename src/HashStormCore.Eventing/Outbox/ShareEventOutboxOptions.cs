namespace HashStormCore.Eventing.Outbox;

public class ShareEventOutboxOptions
{
    public string Directory { get; set; } = "data/event-outbox";
    public long SegmentMaxBytes { get; set; } = 67_108_864;
    public int WriterFlushEvents { get; set; } = 256;
    public int WriterFlushBytes { get; set; } = 524_288;
    public int WriterFlushMs { get; set; } = 100;
    public string FsyncMode { get; set; } = "periodic";
    public int FsyncIntervalMs { get; set; } = 1000;
    public long SoftBacklogBytes { get; set; } = 1_073_741_824;
    public long CriticalBacklogBytes { get; set; } = 10_737_418_240;
    public int PublisherMaxEvents { get; set; } = 256;
    public int PublisherMaxApproxBytes { get; set; } = 524_288;
    public int PublishRetryDelayMs { get; set; } = 250;
    public int PublishMaxRetryDelayMs { get; set; } = 5000;
}
