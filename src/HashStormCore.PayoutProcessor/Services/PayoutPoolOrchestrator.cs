using Microsoft.Extensions.Logging;

namespace HashStormCore.PayoutProcessor.Services;

public class PayoutPoolOrchestrator
{
    public PayoutPoolOrchestrator(ILogger<PayoutPoolOrchestrator> logger)
    {
        this.logger = logger;
    }

    private readonly ILogger<PayoutPoolOrchestrator> logger;

    public Task RunReservationTickAsync(string poolId, CancellationToken ct)
    {
        logger.LogInformation("Dry-run payout reservation tick for pool {PoolId}: no DB mutation performed", poolId);
        return Task.CompletedTask;
    }

    public Task RunPlanningTickAsync(string poolId, CancellationToken ct)
    {
        logger.LogInformation("Dry-run payout planning tick for pool {PoolId}: no DB mutation performed", poolId);
        return Task.CompletedTask;
    }

    public Task RunExecutionTickAsync(string poolId, CancellationToken ct)
    {
        logger.LogInformation("Dry-run payout execution tick for pool {PoolId}: no sender/RPC/DB mutation performed", poolId);
        return Task.CompletedTask;
    }

    public Task RunStaleReconciliationTickAsync(string poolId, CancellationToken ct)
    {
        logger.LogInformation("Dry-run stale sending reconciliation tick for pool {PoolId}: no DB mutation performed", poolId);
        return Task.CompletedTask;
    }

    public Task RunOperationIdReconciliationTickAsync(string poolId, CancellationToken ct)
    {
        logger.LogInformation("Dry-run operation-id reconciliation tick for pool {PoolId}: no provider/RPC/DB mutation performed", poolId);
        return Task.CompletedTask;
    }

    public Task RunSettlementTickAsync(string poolId, CancellationToken ct)
    {
        logger.LogInformation("Dry-run payout settlement tick for pool {PoolId}: no accounting mutation performed", poolId);
        return Task.CompletedTask;
    }
}
