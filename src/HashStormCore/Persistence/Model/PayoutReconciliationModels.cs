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
