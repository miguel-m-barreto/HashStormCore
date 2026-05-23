using HashStormCore.Persistence.Model;

namespace HashStormCore.Payments;

public record PayoutPlanningRunnerRequest
{
    public string PoolId { get; init; }
    public int MaxBatches { get; init; }
    public DateTime Created { get; init; }
}

public record PayoutPlanningRunnerResult
{
    public int CandidateBatchCount { get; init; }
    public int PlannedBatchCount { get; init; }
    public int SkippedBatchCount { get; init; }
    public IReadOnlyCollection<PayoutPlanningSkippedBatch> SkippedBatches { get; init; } =
        Array.Empty<PayoutPlanningSkippedBatch>();
    public IReadOnlyCollection<CreatePayoutSendAttemptsResult> Results { get; init; } =
        Array.Empty<CreatePayoutSendAttemptsResult>();
}

public record PayoutPlanningSkippedBatch
{
    public long BatchId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string CoinFamily { get; init; }
    public string Handler { get; init; }
    public string SendShape { get; init; }
    public string Reason { get; init; }
}
