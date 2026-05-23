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
    public IReadOnlyCollection<CreatePayoutSendAttemptsResult> Results { get; init; } =
        Array.Empty<CreatePayoutSendAttemptsResult>();
}
