namespace HashStormCore.Eventing.Configuration;

public class EventPipelineConfig
{
    public bool Enabled { get; set; }
    public EventPipelineBrokerConfig Broker { get; set; } = new();
    public EventPipelineBatchingConfig Batching { get; set; } = new();
    public EventPipelineLiveConfig Live { get; set; } = new();
    public EventPipelineRetentionConfig Retention { get; set; } = new();
    public EventPipelineHandoffConfig Handoff { get; set; } = new();
    public EventPipelineOutboxConfig Outbox { get; set; } = new();
}

public class EventPipelineLiveConfig
{
    public int WindowSeconds { get; set; } = 600;
    public int RedisTtlSeconds { get; set; } = 900;
    public int BucketSeconds { get; set; } = 10;
}

public class EventPipelineRetentionConfig
{
    public long SoftStreamLengthWarning { get; set; } = 1_000_000;
    public long CriticalStreamLengthWarning { get; set; } = 10_000_000;
}

public class EventPipelineHandoffConfig
{
    public int SoftMaxBufferedEvents { get; set; } = 100_000;
    public long SoftMaxBufferedBytes { get; set; } = 268_435_456;
    public int CriticalBufferedEvents { get; set; } = 500_000;
    public long CriticalBufferedBytes { get; set; } = 1_073_741_824;
}

public class EventPipelineOutboxConfig
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
}
