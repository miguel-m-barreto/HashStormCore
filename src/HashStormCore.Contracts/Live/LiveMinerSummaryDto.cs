namespace HashStormCore.Contracts.Live;

public class LiveMinerSummaryDto
{
    public string PoolId { get; set; } = string.Empty;
    public string Miner { get; set; } = string.Empty;
    public double Hashrate { get; set; }
    public double Difficulty { get; set; }
    public long Accepted { get; set; }
    public long Rejected { get; set; }
    public long Stale { get; set; }
    public int WorkersOnline { get; set; }
    public DateTime LastSeen { get; set; }
}
