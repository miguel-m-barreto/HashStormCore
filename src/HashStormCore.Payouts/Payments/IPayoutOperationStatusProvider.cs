using HashStormCore.Persistence.Model;

namespace HashStormCore.Payments;

public interface IPayoutOperationStatusProvider
{
    Task<PayoutOperationStatusResult> GetOperationStatusAsync(PayoutReconciliationAttemptSummary attempt,
        string operationId, CancellationToken ct);
}
