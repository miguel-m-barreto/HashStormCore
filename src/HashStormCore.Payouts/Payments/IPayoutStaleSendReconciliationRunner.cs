using HashStormCore.Persistence.Model;

namespace HashStormCore.Payments;

public interface IPayoutStaleSendReconciliationRunner
{
    Task<PayoutStaleSendReconciliationResult> ReconcileStaleSendingAsync(
        PayoutStaleSendReconciliationRunnerRequest request, CancellationToken ct);
}
