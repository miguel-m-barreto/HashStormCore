namespace HashStormCore.Payments;

public interface IPayoutOperationIdReconciliationRunner
{
    Task<PayoutOperationIdReconciliationRunnerResult> ReconcileOperationIdsAsync(
        PayoutOperationIdReconciliationRunnerRequest request, CancellationToken ct);
}
