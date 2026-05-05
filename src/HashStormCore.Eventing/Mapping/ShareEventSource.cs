namespace HashStormCore.Eventing.Mapping;

public class ShareEventSource
{
    public string PoolId { get; set; } = string.Empty;
    public string CoinSymbol { get; set; } = string.Empty;
    public string CoinFamily { get; set; } = string.Empty;
    public string Miner { get; set; } = string.Empty;
    public string Worker { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public long? BlockHeight { get; set; }
    public double Difficulty { get; set; }
    public double NetworkDifficulty { get; set; }
    public double ShareMultiplier { get; set; } = 1d;
    public bool IsBlockCandidate { get; set; }
    public string BlockHash { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string TransactionConfirmationData { get; set; } = string.Empty;
    public decimal BlockReward { get; set; }
    public string BlockType { get; set; } = string.Empty;
}
