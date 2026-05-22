using System.Globalization;
using HashStormCore.PayoutProcessor.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HashStormCore.PayoutProcessor.Services;

public class PayoutProcessorService : BackgroundService
{
    public PayoutProcessorService(PayoutProcessorConfig config, PayoutProcessorClusterConfig clusterConfig,
        PayoutPoolOrchestrator orchestrator, ILogger<PayoutProcessorService> logger)
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

        if(config.Mode == PayoutProcessorMode.DbMutating)
        {
            logger.LogCritical("PayoutProcessor DbMutating mode is not implemented in this patch");
            throw new InvalidOperationException("PayoutProcessor DbMutating mode is not implemented");
        }

        if(config.Mode != PayoutProcessorMode.DryRun)
            throw new InvalidOperationException($"Unsupported PayoutProcessor mode '{config.Mode}'");

        logger.LogWarning("PayoutProcessor dry-run mode performs no DB mutations, no settlement, and no wallet/daemon/RPC calls");

        var pools = DiscoverPools();

        if(pools.Count == 0)
        {
            logger.LogWarning("PayoutProcessor dry-run discovered no enabled payout-capable pools; per-pool loop ticks will not run");
            await WaitUntilCancelledAsync(stoppingToken);
            return;
        }

        logger.LogInformation("PayoutProcessor dry-run discovered {PoolCount} payout-capable pool(s)", pools.Count);

        foreach(var pool in pools)
        {
            logger.LogInformation(
                "PayoutProcessor dry-run pool {PoolId}: coin={Coin}, minimumPayment={MinimumPayment}, rewardRecipients={RewardRecipientCount}",
                pool.Id, pool.Coin, pool.MinimumPayment, pool.RewardRecipients.Count);

            foreach(var recipient in pool.RewardRecipients)
            {
                logger.LogInformation(
                    "PayoutProcessor dry-run reward recipient for pool {PoolId}: address={Address}, type={Type}, percentage={Percentage}, minimumPayment={MinimumPaymentSummary}",
                    pool.Id, recipient.Address, recipient.Type ?? string.Empty, recipient.Percentage,
                    FormatRewardRecipientMinimumPayment(recipient.MinimumPayment));
            }
        }

        await RunDryRunLoopsAsync(pools, stoppingToken);
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

            var rewardRecipients = DiscoverRewardRecipients(poolId, pool.RewardRecipients);
            discoveredPools.Add(new PayoutProcessorPoolConfig(poolId, pool.Coin.Trim(),
                pool.PaymentProcessing.MinimumPayment, rewardRecipients));
        }

        return discoveredPools;
    }

    private IReadOnlyCollection<PayoutProcessorRewardRecipientConfig> DiscoverRewardRecipients(string poolId,
        IReadOnlyCollection<PayoutProcessorClusterRewardRecipientConfig>? rewardRecipients)
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
