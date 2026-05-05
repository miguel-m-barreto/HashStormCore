namespace HashStormCore.Eventing.Configuration;

public class EventPipelineBatchingConfig
{
    public int MaxEvents { get; set; } = 256;
    public int MaxApproxBytes { get; set; } = 524_288;
    public int MaxDelayMs { get; set; } = 1000;
}
