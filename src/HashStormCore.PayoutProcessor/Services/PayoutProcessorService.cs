using System.Globalization;
using HashStormCore.PayoutProcessor.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HashStormCore.PayoutProcessor.Services;

public class PayoutProcessorService : BackgroundService
{
    private const string PayoutEngineLegacy = "legacy";
    private const string PayoutEngineIntent = "intent";

    public PayoutProcessorService(PayoutProcessorConfig config, PayoutProcessorClusterConfig clusterConfig,
        PayoutPoolOrchestrator orchestrator,
        ILogger<PayoutProcessorService> logger)
    {
        this.config = config;
        this.clusterConfig = clusterConfig;
        this.orchestrator = orchestrator;
        this.logger = logger;
    }

    private readonly PayoutProcessorConfig config;
    private readonly PayoutProcessorClusterConfig clusterConfig;
    private readonly PayoutPoolOrchestrator orchestrator;
    private readonly ILogger<PayoutProcessorService> logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "PayoutProcessor starting with enabled={Enabled}, mode={Mode}, pools={PoolCount}, fakeAdaptersOnly={FakeAdaptersOnly}",
            config.Enabled, config.Mode, (config.Pools ?? Array.Empty<string>()).Length, config.FakeAdaptersOnly);

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

        if(config.Mode != PayoutProcessorMode.DryRun && config.Mode != PayoutProcessorMode.DbMutating)
            throw new InvalidOperationException($"Unsupported PayoutProcessor mode '{config.Mode}'");

        var pools = DiscoverPools();

        if(pools.Count == 0)
        {
            logger.LogWarning("PayoutProcessor discovered no enabled payout-capable pools; per-pool loop ticks will not run");
            await WaitUntilCancelledAsync(stoppingToken);
            return;
        }

        if(config.Mode == PayoutProcessorMode.DryRun)
            logger.LogWarning("PayoutProcessor dry-run mode performs no DB mutations, no settlement, and no wallet/daemon/RPC calls");
        else
        {
            logger.LogWarning(
                "PayoutProcessor DbMutating mode is enabled for reservation, planning, no-sender-safe execution, and local stale sending quarantine. Operation-id reconciliation, settlement, and wallet/daemon/RPC calls remain disabled");
            logger.LogWarning("PayoutProcessor DbMutating reservation, planning, execution, and stale sending quarantine process only pools with paymentProcessing.engine=intent");
        }

        logger.LogInformation("PayoutProcessor discovered {PoolCount} payout-capable pool(s)", pools.Count);

        foreach(var pool in pools)
        {
            logger.LogInformation(
                "PayoutProcessor pool {PoolId}: coin={Coin}, engine={Engine}, minimumPayment={MinimumPayment}, rewardRecipients={RewardRecipientCount}",
                pool.Id, pool.Coin, pool.Engine, pool.MinimumPayment, pool.RewardRecipients.Count);

            foreach(var recipient in pool.RewardRecipients)
            {
                logger.LogInformation(
                    "PayoutProcessor reward recipient for pool {PoolId}: address={Address}, type={Type}, percentage={Percentage}, minimumPayment={MinimumPaymentSummary}",
                    pool.Id, recipient.Address, recipient.Type ?? string.Empty, recipient.Percentage,
                    FormatRewardRecipientMinimumPayment(recipient.MinimumPayment));
            }
        }

        if(config.Mode == PayoutProcessorMode.DryRun)
            await RunDryRunLoopsAsync(pools, stoppingToken);
        else
            await RunDbMutatingReservationPlanningExecutionAndStaleReconciliationLoopsAsync(pools, stoppingToken);
    }

    private IReadOnlyCollection<PayoutProcessorPoolConfig> DiscoverPools()
    {
        var allowlist = (config.Pools ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seenPoolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discoveredPools = new List<PayoutProcessorPoolConfig>();

        foreach(var pool in clusterConfig.Pools ?? Array.Empty<PayoutProcessorClusterPoolConfig>())
        {
            if(string.IsNullOrWhiteSpace(pool.Id))
            {
                logger.LogWarning("Skipping cluster pool with missing id");
                continue;
            }

            var poolId = pool.Id.Trim();

            if(!seenPoolIds.Add(poolId))
                throw new InvalidOperationException($"Duplicate pool id '{poolId}' in cluster config");

            if(allowlist.Count > 0 && !allowlist.Contains(poolId))
                continue;

            if(!pool.Enabled)
            {
                logger.LogInformation("Skipping pool {PoolId}: pool is disabled", poolId);
                continue;
            }

            if(pool.PaymentProcessing == null)
            {
                logger.LogInformation("Skipping pool {PoolId}: paymentProcessing is missing", poolId);
                continue;
            }

            if(!pool.PaymentProcessing.Enabled)
            {
                logger.LogInformation("Skipping pool {PoolId}: paymentProcessing is disabled", poolId);
                continue;
            }

            if(pool.PaymentProcessing.MinimumPayment < 0)
                throw new InvalidOperationException($"Pool '{poolId}' has negative paymentProcessing.minimumPayment");

            if(string.IsNullOrWhiteSpace(pool.Coin))
                throw new InvalidOperationException($"Pool '{poolId}' has missing coin");

            var engine = ResolvePayoutEngine(poolId, pool.PaymentProcessing.Engine);
            var rewardRecipients = DiscoverRewardRecipients(poolId, pool.RewardRecipients);
            discoveredPools.Add(new PayoutProcessorPoolConfig(poolId, pool.Coin.Trim(),
                engine, pool.PaymentProcessing.MinimumPayment, rewardRecipients));
        }

        return discoveredPools;
    }

    private static string ResolvePayoutEngine(string poolId, string engine)
    {
        if(string.IsNullOrWhiteSpace(engine))
            return PayoutEngineLegacy;

        var trimmedEngine = engine.Trim();

        if(string.Equals(trimmedEngine, PayoutEngineLegacy, StringComparison.OrdinalIgnoreCase))
            return PayoutEngineLegacy;

        if(string.Equals(trimmedEngine, PayoutEngineIntent, StringComparison.OrdinalIgnoreCase))
            return PayoutEngineIntent;

        throw new InvalidOperationException(
            $"Pool '{poolId}' has invalid paymentProcessing.engine '{trimmedEngine}'");
    }

    private IReadOnlyCollection<PayoutProcessorRewardRecipientConfig> DiscoverRewardRecipients(string poolId,
        IReadOnlyCollection<PayoutProcessorClusterRewardRecipientConfig> rewardRecipients)
    {
        var results = new List<PayoutProcessorRewardRecipientConfig>();

        foreach(var recipient in rewardRecipients ?? Array.Empty<PayoutProcessorClusterRewardRecipientConfig>())
        {
            if(recipient.MinimumPayment.HasValue && recipient.MinimumPayment.Value < 0)
                throw new InvalidOperationException($"Pool '{poolId}' has reward recipient with negative minimumPayment");

            if(string.IsNullOrWhiteSpace(recipient.Address))
            {
                logger.LogWarning("Skipping reward recipient with missing address for pool {PoolId}", poolId);
                continue;
            }

            results.Add(new PayoutProcessorRewardRecipientConfig(recipient.Address.Trim(), recipient.Percentage,
                recipient.Type, recipient.MinimumPayment));
        }

        return results;
    }

    private static string FormatRewardRecipientMinimumPayment(decimal? minimumPayment)
    {
        return minimumPayment switch
        {
            null => "inherited",
            0m => "0 (pay positive balance)",
            _ => minimumPayment.Value.ToString(CultureInfo.InvariantCulture)
        };
    }

    private async Task RunDryRunLoopsAsync(IReadOnlyCollection<PayoutProcessorPoolConfig> pools, CancellationToken ct)
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
                    foreach(var pool in pools)
                        await orchestrator.RunReservationTickAsync(pool, ct);

                    nextReservation = now + reservationInterval;
                }

                if(now >= nextPlanning)
                {
                    foreach(var pool in pools)
                        await orchestrator.RunPlanningTickAsync(pool, ct);

                    nextPlanning = now + planningInterval;
                }

                if(now >= nextExecution)
                {
                    foreach(var pool in pools)
                        await orchestrator.RunExecutionTickAsync(pool, ct);

                    nextExecution = now + executionInterval;
                }

                if(now >= nextStaleReconciliation)
                {
                    foreach(var pool in pools)
                        await orchestrator.RunStaleReconciliationTickAsync(pool, ct);

                    nextStaleReconciliation = now + staleReconciliationInterval;
                }

                if(now >= nextOperationIdReconciliation)
                {
                    foreach(var pool in pools)
                        await orchestrator.RunOperationIdReconciliationTickAsync(pool, ct);

                    nextOperationIdReconciliation = now + operationIdReconciliationInterval;
                }

                if(now >= nextSettlement)
                {
                    foreach(var pool in pools)
                        await orchestrator.RunSettlementTickAsync(pool, ct);

                    nextSettlement = now + settlementInterval;
                }
            }
        }

        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            logger.LogInformation("PayoutProcessor dry-run loop stopped");
        }
    }

    private async Task RunDbMutatingReservationPlanningExecutionAndStaleReconciliationLoopsAsync(IReadOnlyCollection<PayoutProcessorPoolConfig> pools,
        CancellationToken ct)
    {
        var reservationInterval = GetInterval(config.ReservationIntervalSeconds);
        var planningInterval = GetInterval(config.PlanningIntervalSeconds);
        var executionInterval = GetInterval(config.ExecutionIntervalSeconds);
        var staleReconciliationInterval = GetInterval(config.StaleReconciliationIntervalSeconds);
        var nextReservation = DateTimeOffset.MinValue;
        var nextPlanning = DateTimeOffset.MinValue;
        var nextExecution = DateTimeOffset.MinValue;
        var nextStaleReconciliation = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while(await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTimeOffset.UtcNow;

                if(now < nextReservation)
                {
                    if(now >= nextPlanning)
                    {
                        foreach(var pool in pools)
                            await RunPoolTickAsync(pool, () => orchestrator.RunPlanningTickAsync(pool, ct),
                                "Payout planning tick failed for pool {PoolId}", ct);

                        nextPlanning = now + planningInterval;
                    }

                    if(now >= nextExecution)
                    {
                        foreach(var pool in pools)
                            await RunPoolTickAsync(pool, () => orchestrator.RunExecutionTickAsync(pool, ct),
                                "Payout execution tick failed for pool {PoolId}", ct);

                        nextExecution = now + executionInterval;
                    }

                    if(now >= nextStaleReconciliation)
                    {
                        foreach(var pool in pools)
                            await RunPoolTickAsync(pool, () => orchestrator.RunStaleReconciliationTickAsync(pool, ct),
                                "Payout stale sending reconciliation tick failed for pool {PoolId}", ct);

                        nextStaleReconciliation = now + staleReconciliationInterval;
                    }

                    continue;
                }

                foreach(var pool in pools)
                    await RunPoolTickAsync(pool, () => orchestrator.RunReservationTickAsync(pool, ct),
                        "Payout reservation tick failed for pool {PoolId}", ct);

                nextReservation = now + reservationInterval;

                if(now >= nextPlanning)
                {
                    foreach(var pool in pools)
                        await RunPoolTickAsync(pool, () => orchestrator.RunPlanningTickAsync(pool, ct),
                            "Payout planning tick failed for pool {PoolId}", ct);

                    nextPlanning = now + planningInterval;
                }

                if(now >= nextExecution)
                {
                    foreach(var pool in pools)
                        await RunPoolTickAsync(pool, () => orchestrator.RunExecutionTickAsync(pool, ct),
                            "Payout execution tick failed for pool {PoolId}", ct);

                    nextExecution = now + executionInterval;
                }

                if(now >= nextStaleReconciliation)
                {
                    foreach(var pool in pools)
                        await RunPoolTickAsync(pool, () => orchestrator.RunStaleReconciliationTickAsync(pool, ct),
                            "Payout stale sending reconciliation tick failed for pool {PoolId}", ct);

                    nextStaleReconciliation = now + staleReconciliationInterval;
                }
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
        }
    }

    private async Task RunPoolTickAsync(PayoutProcessorPoolConfig pool, Func<Task> tick, string errorMessage,
        CancellationToken ct)
    {
        try
        {
            await tick();
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception ex)
        {
            logger.LogError(ex, errorMessage, pool.Id);
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
