namespace HashStormCore.Contracts.Live;

public class LivePoolStatusDto
{
    public string PoolId { get; set; } = string.Empty;
    public string Status { get; set; } = "warming_up";
    public DateTime Updated { get; set; }
    public int WindowSeconds { get; set; }
    public int AvailableWindowSeconds { get; set; }
}
