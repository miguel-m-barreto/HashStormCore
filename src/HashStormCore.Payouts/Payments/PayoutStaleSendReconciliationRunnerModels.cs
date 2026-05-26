namespace HashStormCore.Payments;

public record PayoutStaleSendReconciliationRunnerRequest
{
    public string PoolId { get; init; }
    public DateTime OlderThan { get; init; }
    public DateTime Updated { get; init; }
    public int Limit { get; init; }
    public string ErrorCode { get; init; }
    public string ErrorMessage { get; init; }
}
