using HashStormCore.Persistence.Model;

namespace HashStormCore.Payments;

public record PayoutSettlementRunnerRequest
{
    public string PoolId { get; init; }
    public int Limit { get; init; }
    public DateTime SettledAt { get; init; }
}

public record PayoutSettlementRunnerResult
{
    public int CandidateCount { get; init; }
    public int SettledCount { get; init; }
    public int AlreadySettledCount { get; init; }
    public int SkippedCount { get; init; }
    public int FailureCount { get; init; }
    public IReadOnlyCollection<PayoutSettlementResult> SettlementResults { get; init; } =
        Array.Empty<PayoutSettlementResult>();
    public IReadOnlyCollection<PayoutSettlementSkippedCandidate> SkippedCandidates { get; init; } =
        Array.Empty<PayoutSettlementSkippedCandidate>();
    public IReadOnlyCollection<PayoutSettlementAttemptFailure> Failures { get; init; } =
        Array.Empty<PayoutSettlementAttemptFailure>();
}

public record PayoutSettlementSkippedCandidate
{
    public long BatchId { get; init; }
    public long AttemptId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string Method { get; init; }
    public string EvidenceKind { get; init; }
    public string Status { get; init; }
    public string Reason { get; init; }
}

public record PayoutSettlementAttemptFailure
{
    public long BatchId { get; init; }
    public long AttemptId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string Method { get; init; }
    public string EvidenceKind { get; init; }
    public string ErrorType { get; init; }
}
