namespace HashStormCore.Payments;

public interface IPayoutExecutionRunner
{
    Task<PayoutExecutionRunnerResult> ExecutePreparedAttemptsAsync(PayoutExecutionRunnerRequest request,
        CancellationToken ct);
}
