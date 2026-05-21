namespace HashStormCore.Persistence.Model;

public record PayoutPlanningBatchCandidate
{
    public long BatchId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string CoinFamily { get; init; }
    public string Handler { get; init; }
    public string SendShape { get; init; }
    public int IntentCountSnapshot { get; init; }
    public decimal ReservedAmountSnapshot { get; init; }
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
}

public record PayoutSettlementAttemptCandidate
{
    public long BatchId { get; init; }
    public long AttemptId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string Method { get; init; }
    public string TransactionConfirmationData { get; init; }
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
    public int SubmittedIntentCount { get; init; }
    public int UnsettledSubmittedIntentCount { get; init; }
    public int SettledIntentCount { get; init; }
}
