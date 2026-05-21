namespace HashStormCore.Persistence.Model;

public enum PayoutSettlementStatus
{
    Settled,
    AlreadySettled,
    AttemptNotEligible,
    InsufficientEvidence,
    InsufficientBalance
}

public record PayoutSettlementRequest
{
    public string PoolId { get; init; }
    public long BatchId { get; init; }
    public long AttemptId { get; init; }
    public DateTime SettledAt { get; init; }
}

#nullable enable annotations
public record PayoutSettlementResult
{
    public PayoutSettlementStatus Status { get; init; }
    public long BatchId { get; init; }
    public long AttemptId { get; init; }
    public IReadOnlyCollection<long> SettledIntentIds { get; init; } = Array.Empty<long>();
    public IReadOnlyCollection<long> PaymentIds { get; init; } = Array.Empty<long>();
    public IReadOnlyCollection<long> BalanceChangeIds { get; init; } = Array.Empty<long>();
    public string? TransactionConfirmationData { get; init; }

    public static PayoutSettlementResult Settled(long batchId, long attemptId, IReadOnlyCollection<long> settledIntentIds,
        IReadOnlyCollection<long> paymentIds, IReadOnlyCollection<long> balanceChangeIds, string transactionConfirmationData)
    {
        return new PayoutSettlementResult
        {
            Status = PayoutSettlementStatus.Settled,
            BatchId = batchId,
            AttemptId = attemptId,
            SettledIntentIds = settledIntentIds,
            PaymentIds = paymentIds,
            BalanceChangeIds = balanceChangeIds,
            TransactionConfirmationData = transactionConfirmationData
        };
    }

    public static PayoutSettlementResult AlreadySettled(long batchId, long attemptId, IReadOnlyCollection<long> settledIntentIds,
        IReadOnlyCollection<long> paymentIds, IReadOnlyCollection<long> balanceChangeIds, string transactionConfirmationData)
    {
        return new PayoutSettlementResult
        {
            Status = PayoutSettlementStatus.AlreadySettled,
            BatchId = batchId,
            AttemptId = attemptId,
            SettledIntentIds = settledIntentIds,
            PaymentIds = paymentIds,
            BalanceChangeIds = balanceChangeIds,
            TransactionConfirmationData = transactionConfirmationData
        };
    }

    public static PayoutSettlementResult AttemptNotEligible(long batchId, long attemptId)
    {
        return new PayoutSettlementResult
        {
            Status = PayoutSettlementStatus.AttemptNotEligible,
            BatchId = batchId,
            AttemptId = attemptId
        };
    }

    public static PayoutSettlementResult InsufficientEvidence(long batchId, long attemptId)
    {
        return new PayoutSettlementResult
        {
            Status = PayoutSettlementStatus.InsufficientEvidence,
            BatchId = batchId,
            AttemptId = attemptId
        };
    }

    public static PayoutSettlementResult InsufficientBalance(long batchId, long attemptId)
    {
        return new PayoutSettlementResult
        {
            Status = PayoutSettlementStatus.InsufficientBalance,
            BatchId = batchId,
            AttemptId = attemptId
        };
    }
}
#nullable restore
