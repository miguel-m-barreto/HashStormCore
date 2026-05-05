namespace HashStormCore.Contracts.Live;

public class LiveTopMinerDto
{
    public string Miner { get; set; } = string.Empty;
    public double Hashrate { get; set; }
    public double Difficulty { get; set; }
    public long Accepted { get; set; }
    public DateTime LastSeen { get; set; }
}
