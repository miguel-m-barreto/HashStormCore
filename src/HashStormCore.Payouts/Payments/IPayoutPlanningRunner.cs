namespace HashStormCore.Payments;

public interface IPayoutPlanningRunner
{
    Task<PayoutPlanningRunnerResult> CreateSendAttemptsAsync(PayoutPlanningRunnerRequest request,
        CancellationToken ct);
}
