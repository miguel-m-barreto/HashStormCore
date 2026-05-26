namespace HashStormCore.Payments;

public interface IPayoutSettlementRunner
{
    Task<PayoutSettlementRunnerResult> SettleAcceptedAttemptsAsync(PayoutSettlementRunnerRequest request,
        CancellationToken ct);
}
