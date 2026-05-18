namespace HashStormCore.Persistence.Model;

public record PayoutSendExecutionContext
{
    public PayoutBatch Batch { get; init; }
    public PayoutSendAttempt Attempt { get; init; }
    public IReadOnlyCollection<PayoutSendExecutionIntent> Intents { get; init; } = Array.Empty<PayoutSendExecutionIntent>();
}

public record PayoutSendExecutionIntent
{
    public long IntentId { get; init; }
    public long AttemptId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string Address { get; init; }
    public decimal Amount { get; init; }
    public string IntentState { get; init; }
    public string AttemptIntentState { get; init; }
}
