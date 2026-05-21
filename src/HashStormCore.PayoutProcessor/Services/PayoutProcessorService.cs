using HashStormCore.PayoutProcessor.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HashStormCore.PayoutProcessor.Services;

public class PayoutProcessorService : BackgroundService
{
    public PayoutProcessorService(PayoutProcessorConfig config, PayoutPoolOrchestrator orchestrator,
        ILogger<PayoutProcessorService> logger)
    {
        this.config = config;
        this.orchestrator = orchestrator;
        this.logger = logger;
    }

    private readonly PayoutProcessorConfig config;
    private readonly PayoutPoolOrchestrator orchestrator;
    private readonly ILogger<PayoutProcessorService> logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "PayoutProcessor starting with enabled={Enabled}, mode={Mode}, pools={PoolCount}, fakeAdaptersOnly={FakeAdaptersOnly}",
            config.Enabled, config.Mode, config.Pools.Length, config.FakeAdaptersOnly);

        logger.LogInformation(
            "PayoutProcessor intervals: reservation={Reservation}s, planning={Planning}s, execution={Execution}s, staleReconciliation={Stale}s, operationIdReconciliation={OperationId}s, settlement={Settlement}s",
            config.ReservationIntervalSeconds,
            config.PlanningIntervalSeconds,
            config.ExecutionIntervalSeconds,
            config.StaleReconciliationIntervalSeconds,
            config.OperationIdReconciliationIntervalSeconds,
            config.SettlementIntervalSeconds);

        if(!config.Enabled || config.Mode == PayoutProcessorMode.Disabled)
        {
            logger.LogInformation("PayoutProcessor sidecar is disabled");
            return;
        }

        if(config.Mode == PayoutProcessorMode.DbMutating)
        {
            logger.LogCritical("PayoutProcessor DbMutating mode is not implemented in this patch");
            throw new InvalidOperationException("PayoutProcessor DbMutating mode is not implemented");
        }

        if(config.Mode != PayoutProcessorMode.DryRun)
            throw new InvalidOperationException($"Unsupported PayoutProcessor mode '{config.Mode}'");

        logger.LogWarning("PayoutProcessor dry-run mode performs no DB mutations, no settlement, and no wallet/daemon/RPC calls");

        var pools = config.Pools
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if(pools.Length == 0)
        {
            logger.LogWarning("PayoutProcessor dry-run has no pool allowlist configured; per-pool loop ticks will not run");
            await WaitUntilCancelledAsync(stoppingToken);
            return;
        }

        await RunDryRunLoopsAsync(pools, stoppingToken);
    }

    private async Task RunDryRunLoopsAsync(IReadOnlyCollection<string> pools, CancellationToken ct)
    {
        var reservationInterval = GetInterval(config.ReservationIntervalSeconds);
        var planningInterval = GetInterval(config.PlanningIntervalSeconds);
        var executionInterval = GetInterval(config.ExecutionIntervalSeconds);
        var staleReconciliationInterval = GetInterval(config.StaleReconciliationIntervalSeconds);
        var operationIdReconciliationInterval = GetInterval(config.OperationIdReconciliationIntervalSeconds);
        var settlementInterval = GetInterval(config.SettlementIntervalSeconds);

        var nextReservation = DateTimeOffset.MinValue;
        var nextPlanning = DateTimeOffset.MinValue;
        var nextExecution = DateTimeOffset.MinValue;
        var nextStaleReconciliation = DateTimeOffset.MinValue;
        var nextOperationIdReconciliation = DateTimeOffset.MinValue;
        var nextSettlement = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while(await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTimeOffset.UtcNow;

                if(now >= nextReservation)
                {
                    foreach(var poolId in pools)
                        await orchestrator.RunReservationTickAsync(poolId, ct);

                    nextReservation = now + reservationInterval;
                }

                if(now >= nextPlanning)
                {
                    foreach(var poolId in pools)
                        await orchestrator.RunPlanningTickAsync(poolId, ct);

                    nextPlanning = now + planningInterval;
                }

                if(now >= nextExecution)
                {
                    foreach(var poolId in pools)
                        await orchestrator.RunExecutionTickAsync(poolId, ct);

                    nextExecution = now + executionInterval;
                }

                if(now >= nextStaleReconciliation)
                {
                    foreach(var poolId in pools)
                        await orchestrator.RunStaleReconciliationTickAsync(poolId, ct);

                    nextStaleReconciliation = now + staleReconciliationInterval;
                }

                if(now >= nextOperationIdReconciliation)
                {
                    foreach(var poolId in pools)
                        await orchestrator.RunOperationIdReconciliationTickAsync(poolId, ct);

                    nextOperationIdReconciliation = now + operationIdReconciliationInterval;
                }

                if(now >= nextSettlement)
                {
                    foreach(var poolId in pools)
                        await orchestrator.RunSettlementTickAsync(poolId, ct);

                    nextSettlement = now + settlementInterval;
                }
            }
        }

        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            logger.LogInformation("PayoutProcessor dry-run loop stopped");
        }
    }

    private static async Task WaitUntilCancelledAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
        }
    }

    private static TimeSpan GetInterval(int seconds)
    {
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }
}
