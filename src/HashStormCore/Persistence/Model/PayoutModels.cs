namespace HashStormCore.Persistence.Model;

public record PayoutBatch
{
    public long Id { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string CoinFamily { get; init; }
    public string Handler { get; init; }
    public string State { get; init; }
    public string SendShape { get; init; }
    public string RecipientSetHash { get; init; }
    public decimal MinimumAmount { get; init; }
    public decimal ReservedAmountSnapshot { get; init; }
    public int IntentCountSnapshot { get; init; }
    public string ExternalOperationId { get; init; }
    public string TransactionConfirmationData { get; init; }
    public string ErrorCode { get; init; }
    public string ErrorMessage { get; init; }
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
    public DateTime? Submitted { get; init; }
    public DateTime? Settled { get; init; }
    public DateTime? Reviewed { get; init; }
    public PayoutIntent[] Intents { get; init; } = Array.Empty<PayoutIntent>();
}

public record PayoutIntent
{
    public long Id { get; init; }
    public long BatchId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string Address { get; init; }
    public string State { get; init; }
    public decimal Amount { get; init; }
    public decimal BalanceSnapshotAmount { get; init; }
    public DateTime BalanceSnapshotUpdated { get; init; }
    public decimal PaymentThreshold { get; init; }
    public string TransactionConfirmationData { get; init; }
    public long? PaymentId { get; init; }
    public long? BalanceChangeId { get; init; }
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
    public DateTime? Settled { get; init; }
    public string ErrorCode { get; init; }
    public string ErrorMessage { get; init; }
}

public record PayoutSendAttempt
{
    public long Id { get; init; }
    public long BatchId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public int AttemptNo { get; init; }
    public string Method { get; init; }
    public string State { get; init; }
    public string RequestHash { get; init; }
    public string RequestSummary { get; init; }
    public int RecipientCount { get; init; }
    public decimal AmountSnapshot { get; init; }
    public string ExternalOperationId { get; init; }
    public string TransactionConfirmationData { get; init; }
    public string ErrorCode { get; init; }
    public string ErrorMessage { get; init; }
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
    public DateTime? Completed { get; init; }
    public PayoutAttemptIntent[] AttemptIntents { get; init; } = Array.Empty<PayoutAttemptIntent>();
}

public record PayoutAttemptIntent
{
    public long AttemptId { get; init; }
    public long IntentId { get; init; }
    public long BatchId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string State { get; init; }
    public decimal Amount { get; init; }
    public string TransactionConfirmationData { get; init; }
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
}

public record PayoutExternalConfirmation
{
    public long Id { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public long BatchId { get; init; }
    public long? AttemptId { get; init; }
    public long? IntentId { get; init; }
    public string Kind { get; init; }
    public string Value { get; init; }
    public DateTime Created { get; init; }
}

public record PayoutAdminAction
{
    public long Id { get; init; }
    public string PoolId { get; init; }
    public long? BatchId { get; init; }
    public long? IntentId { get; init; }
    public long? AttemptId { get; init; }
    public string Action { get; init; }
    public string Reason { get; init; }
    public string Operator { get; init; }
    public string Metadata { get; init; }
    public DateTime Created { get; init; }
}

public record CreatePayoutBatchRequest
{
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string CoinFamily { get; init; }
    public string Handler { get; init; }
    public string SendShape { get; init; }
    public string RecipientSetHash { get; init; }
    public decimal MinimumAmount { get; init; }
    public decimal ReservedAmountSnapshot { get; init; }
    public int IntentCountSnapshot { get; init; }
    public DateTime Created { get; init; }
}

public record CreatePayoutIntentRequest
{
    public string Address { get; init; }
    public decimal Amount { get; init; }
    public decimal BalanceSnapshotAmount { get; init; }
    public DateTime BalanceSnapshotUpdated { get; init; }
    public decimal PaymentThreshold { get; init; }
}

public record CreatePayoutSendAttemptRequest
{
    public long BatchId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public int AttemptNo { get; init; }
    public string Method { get; init; }
    public string RequestHash { get; init; }
    public string RequestSummary { get; init; }
    public int RecipientCount { get; init; }
    public decimal AmountSnapshot { get; init; }
    public DateTime Created { get; init; }
}

public record PayoutAttemptEvidence
{
    public string Kind { get; init; }
    public string Value { get; init; }
}
