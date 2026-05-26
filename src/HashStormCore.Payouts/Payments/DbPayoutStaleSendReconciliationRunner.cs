using HashStormCore.Extensions;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;

namespace HashStormCore.Payments;

public class DbPayoutStaleSendReconciliationRunner : IPayoutStaleSendReconciliationRunner
{
    public DbPayoutStaleSendReconciliationRunner(IConnectionFactory connectionFactory,
        PayoutStaleSendReconciliationService payoutStaleSendReconciliationService)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.payoutStaleSendReconciliationService = payoutStaleSendReconciliationService ??
            throw new ArgumentNullException(nameof(payoutStaleSendReconciliationService));
    }

    private readonly IConnectionFactory connectionFactory;
    private readonly PayoutStaleSendReconciliationService payoutStaleSendReconciliationService;

    public Task<PayoutStaleSendReconciliationResult> ReconcileStaleSendingAsync(
        PayoutStaleSendReconciliationRunnerRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateRequest(request);

        return connectionFactory.RunTx((con, tx) =>
            payoutStaleSendReconciliationService.MarkStaleSendingBatchesAmbiguousAsync(con, tx,
                new PayoutStaleSendReconciliationRequest
                {
                    PoolId = request.PoolId,
                    OlderThan = request.OlderThan,
                    Updated = request.Updated,
                    Limit = request.Limit,
                    ErrorCode = request.ErrorCode,
                    ErrorMessage = request.ErrorMessage
                }, ct));
    }

    private static void ValidateRequest(PayoutStaleSendReconciliationRunnerRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(string.IsNullOrWhiteSpace(request.PoolId))
            throw new ArgumentException("Pool id is required", nameof(request));

        if(request.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Limit),
                "Stale send reconciliation limit must be greater than zero");

        if(request.OlderThan == default)
            throw new ArgumentException("OlderThan must be set", nameof(request));

        if(request.Updated == default)
            throw new ArgumentException("Updated must be set", nameof(request));

        if(request.Updated < request.OlderThan)
            throw new ArgumentException("Updated must not be earlier than OlderThan", nameof(request));

        if(string.IsNullOrWhiteSpace(request.ErrorCode))
            throw new ArgumentException("Error code is required", nameof(request));

        if(request.ErrorMessage != null && string.IsNullOrWhiteSpace(request.ErrorMessage))
            throw new ArgumentException("Error message cannot be whitespace", nameof(request));
    }
}
