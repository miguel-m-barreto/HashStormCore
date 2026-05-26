namespace HashStormCore.Payments;

public record PayoutOperationIdReconciliationRunnerRequest
{
    public string PoolId { get; init; }
    public int Limit { get; init; }
    public DateTime CheckedAt { get; init; }
}

public record PayoutOperationIdReconciliationRunnerResult
{
    public int CandidateCount { get; init; }
    public int ProviderPendingCount { get; init; }
    public int EvidenceAttachedCount { get; init; }
    public int AlreadyAttachedCount { get; init; }
    public int NeedsReviewCount { get; init; }
    public int ProviderErrorCount { get; init; }
    public int SkippedCount { get; init; }
    public int FailureCount { get; init; }
    public IReadOnlyCollection<long> EvidenceAttachedAttemptIds { get; init; } = Array.Empty<long>();
    public IReadOnlyCollection<PayoutOperationIdReconciliationSkippedAttempt> SkippedAttempts { get; init; } =
        Array.Empty<PayoutOperationIdReconciliationSkippedAttempt>();
    public IReadOnlyCollection<PayoutOperationIdReconciliationAttemptFailure> Failures { get; init; } =
        Array.Empty<PayoutOperationIdReconciliationAttemptFailure>();
}

public record PayoutOperationIdReconciliationSkippedAttempt
{
    public long BatchId { get; init; }
    public long AttemptId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string CoinFamily { get; init; }
    public string Handler { get; init; }
    public string SendShape { get; init; }
    public string Method { get; init; }
    public string Reason { get; init; }
}

public record PayoutOperationIdReconciliationAttemptFailure
{
    public long BatchId { get; init; }
    public long AttemptId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string CoinFamily { get; init; }
    public string Handler { get; init; }
    public string SendShape { get; init; }
    public string Method { get; init; }
    public string ErrorType { get; init; }
}
