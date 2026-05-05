namespace HashStormCore.Contracts.Live;

public class LivePoolSummaryDto
{
    public string PoolId { get; set; } = string.Empty;
    public double Hashrate { get; set; }
    public double Difficulty { get; set; }
    public long Accepted { get; set; }
    public long Rejected { get; set; }
    public long Stale { get; set; }
    public int MinersOnline { get; set; }
    public int WorkersOnline { get; set; }
    public DateTime Updated { get; set; }
}
