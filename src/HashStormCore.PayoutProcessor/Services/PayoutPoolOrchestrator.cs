using HashStormCore.PayoutProcessor.Configuration;
using Microsoft.Extensions.Logging;

namespace HashStormCore.PayoutProcessor.Services;

public class PayoutPoolOrchestrator
{
    public PayoutPoolOrchestrator(ILogger<PayoutPoolOrchestrator> logger)
    {
        this.logger = logger;
    }

    private readonly ILogger<PayoutPoolOrchestrator> logger;

    public Task RunReservationTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        logger.LogInformation("Dry-run payout reservation tick for pool {PoolId} using engine {Engine}: no DB mutation performed", pool.Id, pool.Engine);
        return Task.CompletedTask;
    }

    public Task RunPlanningTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        logger.LogInformation("Dry-run payout planning tick for pool {PoolId} using engine {Engine}: no DB mutation performed", pool.Id, pool.Engine);
        return Task.CompletedTask;
    }

    public Task RunExecutionTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        logger.LogInformation("Dry-run payout execution tick for pool {PoolId} using engine {Engine}: no sender/RPC/DB mutation performed", pool.Id, pool.Engine);
        return Task.CompletedTask;
    }

    public Task RunStaleReconciliationTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        logger.LogInformation("Dry-run stale sending reconciliation tick for pool {PoolId} using engine {Engine}: no DB mutation performed", pool.Id, pool.Engine);
        return Task.CompletedTask;
    }

    public Task RunOperationIdReconciliationTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        logger.LogInformation("Dry-run operation-id reconciliation tick for pool {PoolId} using engine {Engine}: no provider/RPC/DB mutation performed", pool.Id, pool.Engine);
        return Task.CompletedTask;
    }

    public Task RunSettlementTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        logger.LogInformation("Dry-run payout settlement tick for pool {PoolId} using engine {Engine}: no accounting mutation performed", pool.Id, pool.Engine);
        return Task.CompletedTask;
    }
}
