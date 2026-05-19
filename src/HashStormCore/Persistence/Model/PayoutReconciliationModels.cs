namespace HashStormCore.Persistence.Model;

public record PayoutReconciliationAttemptSummary
{
    public long BatchId { get; init; }
    public long AttemptId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string AttemptState { get; init; }
    public string BatchState { get; init; }
    public string Method { get; init; }
    public string ExternalOperationId { get; init; }
    public string TransactionConfirmationData { get; init; }
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
}

public record PayoutAttemptConfirmationSummary
{
    public long Id { get; init; }
    public long BatchId { get; init; }
    public long? AttemptId { get; init; }
    public long? IntentId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string Kind { get; init; }
    public string Value { get; init; }
    public DateTime Created { get; init; }
}

public record PayoutStaleSendingBatchCandidate
{
    public long BatchId { get; init; }
    public long StaleAttemptId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string BatchState { get; init; }
    public string AttemptState { get; init; }
    public DateTime AttemptUpdated { get; init; }
    public DateTime AttemptCreated { get; init; }
}

public record PayoutStaleSendReconciliationRequest
{
    public string PoolId { get; init; }
    public DateTime OlderThan { get; init; }
    public DateTime Updated { get; init; }
    public int Limit { get; init; }
    public string ErrorCode { get; init; }
    public string ErrorMessage { get; init; }
}

public record PayoutStaleSendReconciliationResult
{
    public int CandidateBatchCount { get; init; }
    public int MarkedBatchCount => MarkedBatchIds.Count;
    public IReadOnlyCollection<long> MarkedBatchIds { get; init; } = Array.Empty<long>();
    public IReadOnlyCollection<long> SkippedBatchIds { get; init; } = Array.Empty<long>();
}

public record PayoutOperationIdReconciliationRequest
{
    public string PoolId { get; init; }
    public int Limit { get; init; }
    public DateTime CheckedAt { get; init; }
}

public record PayoutOperationIdReconciliationResult
{
    public int CandidateCount { get; init; }
    public int ProviderPendingCount { get; init; }
    public int EvidenceAttachedCount { get; init; }
    public int AlreadyAttachedCount { get; init; }
    public int NeedsReviewCount { get; init; }
    public int ProviderErrorCount { get; init; }
    public IReadOnlyCollection<long> EvidenceAttachedAttemptIds { get; init; } = Array.Empty<long>();
}

public enum PayoutOperationStatus
{
    Pending,
    ResolvedTxId,
    ProvenNoAccept,
    Unknown
}

#nullable enable annotations
public record PayoutOperationStatusResult
{
    public PayoutOperationStatus Status { get; init; }
    public string? TxId { get; init; }
    public string? ReasonCode { get; init; }

    public static PayoutOperationStatusResult Pending(string? reasonCode = null)
    {
        return new PayoutOperationStatusResult
        {
            Status = PayoutOperationStatus.Pending,
            ReasonCode = reasonCode
        };
    }

    public static PayoutOperationStatusResult ResolvedTxId(string txId)
    {
        return new PayoutOperationStatusResult
        {
            Status = PayoutOperationStatus.ResolvedTxId,
            TxId = txId
        };
    }

    public static PayoutOperationStatusResult ProvenNoAccept(string? reasonCode = null)
    {
        return new PayoutOperationStatusResult
        {
            Status = PayoutOperationStatus.ProvenNoAccept,
            ReasonCode = reasonCode
        };
    }

    public static PayoutOperationStatusResult Unknown(string? reasonCode = null)
    {
        return new PayoutOperationStatusResult
        {
            Status = PayoutOperationStatus.Unknown,
            ReasonCode = reasonCode
        };
    }
}
#nullable restore
