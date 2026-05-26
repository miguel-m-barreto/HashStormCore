using HashStormCore.PayoutProcessor.Configuration;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using Microsoft.Extensions.Logging;

namespace HashStormCore.PayoutProcessor.Services;

public class PayoutPoolOrchestrator
{
    private const string PayoutEngineIntent = "intent";
    private const string StaleSendingErrorCode = "stale_send_reconciliation";
    private const string StaleSendingErrorMessage =
        "Payout send attempt stayed in sending past stale threshold";

    public PayoutPoolOrchestrator(PayoutProcessorConfig config, IPayoutProfileResolver payoutProfileResolver,
        IPayoutReservationRunner payoutReservationRunner, IPayoutPlanningRunner payoutPlanningRunner,
        IPayoutExecutionRunner payoutExecutionRunner,
        IPayoutStaleSendReconciliationRunner payoutStaleSendReconciliationRunner,
        ILogger<PayoutPoolOrchestrator> logger)
    {
        this.config = config;
        this.payoutProfileResolver = payoutProfileResolver;
        this.payoutReservationRunner = payoutReservationRunner;
        this.payoutPlanningRunner = payoutPlanningRunner;
        this.payoutExecutionRunner = payoutExecutionRunner;
        this.payoutStaleSendReconciliationRunner = payoutStaleSendReconciliationRunner;
        this.logger = logger;
    }

    private readonly PayoutProcessorConfig config;
    private readonly IPayoutProfileResolver payoutProfileResolver;
    private readonly IPayoutReservationRunner payoutReservationRunner;
    private readonly IPayoutPlanningRunner payoutPlanningRunner;
    private readonly IPayoutExecutionRunner payoutExecutionRunner;
    private readonly IPayoutStaleSendReconciliationRunner payoutStaleSendReconciliationRunner;
    private readonly ILogger<PayoutPoolOrchestrator> logger;

    public async Task RunReservationTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if(!config.Enabled)
        {
            logger.LogInformation("Skipping payout reservation for pool {PoolId}: PayoutProcessor is disabled", pool.Id);
            return;
        }

        if(config.Mode == PayoutProcessorMode.Disabled)
        {
            logger.LogInformation("Skipping payout reservation for pool {PoolId}: PayoutProcessor mode is Disabled",
                pool.Id);
            return;
        }

        if(!string.Equals(pool.Engine, PayoutEngineIntent, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Skipping payout reservation for pool {PoolId}: paymentProcessing.engine={Engine}, only engine=intent is processed by PayoutProcessor",
                pool.Id, pool.Engine);
            return;
        }

        if(config.Mode == PayoutProcessorMode.DbMutating && config.ReservationMaxCandidates <= 0)
        {
            logger.LogWarning(
                "Skipping payout reservation for pool {PoolId}: ReservationMaxCandidates must be greater than zero for DbMutating reservation",
                pool.Id);
            return;
        }

        var resolution = payoutProfileResolver.Resolve(pool.Coin);

        if(!resolution.HasProfile)
        {
            logger.LogWarning(
                "Skipping payout reservation for pool {PoolId}: configured coin '{Coin}' is unsupported: {Reason}. Hint: pool.coin must match a coins.json key",
                pool.Id, pool.Coin, resolution.Reason);
            return;
        }

        var profile = resolution.Profile;

        if(!profile.ReservationReady)
        {
            logger.LogWarning(
                "Skipping payout reservation for pool {PoolId}: profile is not reservation-ready for coinKey={CoinKey}, family={Family}, adapterId={AdapterId}, reason={Reason}",
                pool.Id, profile.CoinKey, profile.CoinFamily, profile.AdapterId,
                string.IsNullOrWhiteSpace(profile.NotReadyReason) ? resolution.Reason : profile.NotReadyReason);
            return;
        }

        var request = BuildReservationRequest(pool, profile, config);

        if(config.Mode == PayoutProcessorMode.DryRun)
        {
            logger.LogInformation(
                "Dry-run payout reservation tick for pool {PoolId}: would create reservation with coinKey={Coin}, coinFamily={CoinFamily}, handler={Handler}, sendShape={SendShape}, minimumPayment={MinimumPayment}, maxCandidates={MaxCandidates}, rewardRecipientThresholds={RewardRecipientThresholdCount}",
                request.PoolId, request.Coin, request.CoinFamily, request.Handler, request.SendShape,
                request.MinimumPayment, request.MaxCandidates, request.RewardRecipientThresholds.Count);
            return;
        }

        if(config.Mode != PayoutProcessorMode.DbMutating)
        {
            logger.LogInformation("Payout reservation tick for pool {PoolId} skipped: processor mode is {Mode}", pool.Id,
                config.Mode);
            return;
        }

        logger.LogInformation(
            "DB-mutating payout reservation tick for pool {PoolId}: coinKey={Coin}, coinFamily={CoinFamily}, handler={Handler}, sendShape={SendShape}, maxCandidates={MaxCandidates}",
            request.PoolId, request.Coin, request.CoinFamily, request.Handler, request.SendShape,
            request.MaxCandidates);

        var result = await payoutReservationRunner.CreateReservationAsync(request, ct);

        switch(result.Status)
        {
            case PayoutReservationStatus.Created:
                logger.LogInformation(
                    "Created payout reservation batch {BatchId} for pool {PoolId}: intents={IntentCount}, amount={Amount}",
                    result.Batch.Id, pool.Id, result.Batch.IntentCountSnapshot, result.Batch.ReservedAmountSnapshot);
                break;

            case PayoutReservationStatus.ActiveBatchExists:
                logger.LogInformation("Skipping payout reservation for pool {PoolId}: active batch {BatchId} already exists",
                    pool.Id, result.Batch.Id);
                break;

            case PayoutReservationStatus.NoEligibleBalances:
                logger.LogInformation("No eligible payout balances for pool {PoolId}", pool.Id);
                break;

            default:
                logger.LogWarning("Payout reservation for pool {PoolId} returned unexpected status {Status}", pool.Id,
                    result.Status);
                break;
        }
    }

    public async Task RunPlanningTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if(!config.Enabled)
        {
            logger.LogInformation("Skipping payout planning for pool {PoolId}: PayoutProcessor is disabled", pool.Id);
            return;
        }

        if(config.Mode == PayoutProcessorMode.Disabled)
        {
            logger.LogInformation("Skipping payout planning for pool {PoolId}: PayoutProcessor mode is Disabled",
                pool.Id);
            return;
        }

        if(!string.Equals(pool.Engine, PayoutEngineIntent, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Skipping payout planning for pool {PoolId}: paymentProcessing.engine={Engine}, only engine=intent is processed by PayoutProcessor",
                pool.Id, pool.Engine);
            return;
        }

        if(config.Mode == PayoutProcessorMode.DryRun)
        {
            logger.LogInformation("Dry-run payout planning tick for pool {PoolId}: would plan reserved batches",
                pool.Id);
            return;
        }

        if(config.Mode != PayoutProcessorMode.DbMutating)
        {
            logger.LogInformation("Payout planning tick for pool {PoolId} skipped: processor mode is {Mode}", pool.Id,
                config.Mode);
            return;
        }

        if(config.PlanningMaxBatches <= 0)
        {
            logger.LogWarning(
                "Skipping payout planning for pool {PoolId}: PlanningMaxBatches must be greater than zero for DbMutating planning",
                pool.Id);
            return;
        }

        var request = BuildPlanningRequest(pool, config);

        logger.LogInformation("DB-mutating payout planning tick for pool {PoolId}: maxBatches={MaxBatches}",
            request.PoolId, request.MaxBatches);

        var result = await payoutPlanningRunner.CreateSendAttemptsAsync(request, ct);

        logger.LogInformation(
            "Payout planning tick for pool {PoolId} completed: candidateBatches={CandidateBatchCount}, plannedBatches={PlannedBatchCount}, skippedBatches={SkippedBatchCount}",
            request.PoolId, result.CandidateBatchCount, result.PlannedBatchCount, result.SkippedBatchCount);

        foreach(var skipped in result.SkippedBatches)
        {
            logger.LogWarning(
                "Skipped payout planning for batch {BatchId} in pool {PoolId}: coin={Coin}, coinFamily={CoinFamily}, handler={Handler}, sendShape={SendShape}, reason={Reason}",
                skipped.BatchId, skipped.PoolId, skipped.Coin, skipped.CoinFamily, skipped.Handler,
                skipped.SendShape, skipped.Reason);
        }
    }

    public async Task RunExecutionTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if(!config.Enabled)
        {
            logger.LogInformation("Skipping payout execution for pool {PoolId}: PayoutProcessor is disabled", pool.Id);
            return;
        }

        if(config.Mode == PayoutProcessorMode.Disabled)
        {
            logger.LogInformation("Skipping payout execution for pool {PoolId}: PayoutProcessor mode is Disabled",
                pool.Id);
            return;
        }

        if(!string.Equals(pool.Engine, PayoutEngineIntent, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Skipping payout execution for pool {PoolId}: paymentProcessing.engine={Engine}, only engine=intent is processed by PayoutProcessor",
                pool.Id, pool.Engine);
            return;
        }

        if(config.Mode == PayoutProcessorMode.DryRun)
        {
            logger.LogInformation(
                "Dry-run payout execution tick for pool {PoolId}: would enumerate prepared attempts, maxAttempts={MaxAttempts}",
                pool.Id, config.ExecutionBatchSize);
            return;
        }

        if(config.Mode != PayoutProcessorMode.DbMutating)
        {
            logger.LogInformation("Payout execution tick for pool {PoolId} skipped: processor mode is {Mode}", pool.Id,
                config.Mode);
            return;
        }

        if(config.ExecutionBatchSize <= 0)
        {
            logger.LogWarning(
                "Skipping payout execution for pool {PoolId}: ExecutionBatchSize must be greater than zero for DbMutating execution",
                pool.Id);
            return;
        }

        var request = BuildExecutionRequest(pool, config);

        logger.LogInformation(
            "DB-mutating payout execution tick for pool {PoolId}: maxAttempts={MaxAttempts}",
            request.PoolId, request.MaxAttempts);

        var result = await payoutExecutionRunner.ExecutePreparedAttemptsAsync(request, ct);

        logger.LogInformation(
            "Payout execution tick for pool {PoolId} completed: candidateAttempts={CandidateAttemptCount}, executedAttempts={ExecutedAttemptCount}, skippedAttempts={SkippedAttemptCount}, failedAttempts={FailureCount}",
            request.PoolId, result.CandidateAttemptCount, result.ExecutedAttemptCount, result.SkippedAttemptCount,
            result.FailureCount);

        foreach(var skipped in result.SkippedAttempts)
        {
            logger.LogInformation(
                "Skipped payout execution for attempt {AttemptId} in pool {PoolId}: coin={Coin}, method={Method}, reason={Reason}",
                skipped.AttemptId, skipped.PoolId, skipped.Coin, skipped.Method, skipped.Reason);
        }

        foreach(var failure in result.Failures)
        {
            logger.LogWarning(
                "Payout execution attempt {AttemptId} in pool {PoolId} failed before completion: coin={Coin}, method={Method}, errorType={ErrorType}",
                failure.AttemptId, failure.PoolId, failure.Coin, failure.Method, failure.ErrorType);
        }
    }

    public async Task RunStaleReconciliationTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if(!config.Enabled)
        {
            logger.LogInformation("Skipping stale sending reconciliation for pool {PoolId}: PayoutProcessor is disabled",
                pool.Id);
            return;
        }

        if(config.Mode == PayoutProcessorMode.Disabled)
        {
            logger.LogInformation(
                "Skipping stale sending reconciliation for pool {PoolId}: PayoutProcessor mode is Disabled",
                pool.Id);
            return;
        }

        if(!string.Equals(pool.Engine, PayoutEngineIntent, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Skipping stale sending reconciliation for pool {PoolId}: paymentProcessing.engine={Engine}, only engine=intent is processed by PayoutProcessor",
                pool.Id, pool.Engine);
            return;
        }

        if(config.Mode == PayoutProcessorMode.DryRun)
        {
            logger.LogInformation(
                "Dry-run stale sending reconciliation tick for pool {PoolId}: would quarantine stale sending batches older than {StaleSendingAgeSeconds}s, limit={Limit}",
                pool.Id, config.StaleSendingAgeSeconds, config.ExecutionBatchSize);
            return;
        }

        if(config.Mode != PayoutProcessorMode.DbMutating)
        {
            logger.LogInformation("Stale sending reconciliation tick for pool {PoolId} skipped: processor mode is {Mode}",
                pool.Id, config.Mode);
            return;
        }

        if(config.StaleSendingAgeSeconds <= 0)
        {
            logger.LogWarning(
                "Skipping stale sending reconciliation for pool {PoolId}: StaleSendingAgeSeconds must be greater than zero for DbMutating reconciliation",
                pool.Id);
            return;
        }

        if(config.ExecutionBatchSize <= 0)
        {
            logger.LogWarning(
                "Skipping stale sending reconciliation for pool {PoolId}: ExecutionBatchSize is used as the stale reconciliation limit and must be greater than zero",
                pool.Id);
            return;
        }

        var request = BuildStaleSendReconciliationRequest(pool, config);

        logger.LogInformation(
            "DB-mutating stale sending reconciliation tick for pool {PoolId}: olderThan={OlderThan}, updated={Updated}, limit={Limit}",
            request.PoolId, request.OlderThan, request.Updated, request.Limit);

        var result = await payoutStaleSendReconciliationRunner.ReconcileStaleSendingAsync(request, ct);

        logger.LogInformation(
            "Stale sending reconciliation tick for pool {PoolId} completed: candidateBatches={CandidateBatchCount}, markedBatches={MarkedBatchCount}, skippedBatches={SkippedBatchCount}",
            request.PoolId, result.CandidateBatchCount, result.MarkedBatchCount, result.SkippedBatchIds.Count);

        foreach(var batchId in result.MarkedBatchIds)
        {
            logger.LogInformation("Marked stale sending payout batch {BatchId} ambiguous for pool {PoolId}",
                batchId, request.PoolId);
        }

        foreach(var batchId in result.SkippedBatchIds)
        {
            logger.LogWarning("Skipped stale sending quarantine for payout batch {BatchId} in pool {PoolId}",
                batchId, request.PoolId);
        }
    }

    public Task RunOperationIdReconciliationTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        logger.LogInformation(
            "Operation-id reconciliation tick for pool {PoolId} using engine {Engine}: provider loop is not implemented and no provider/RPC/DB mutation is performed",
            pool.Id, pool.Engine);
        return Task.CompletedTask;
    }

    public Task RunSettlementTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        logger.LogInformation(
            "Payout settlement tick for pool {PoolId} using engine {Engine}: settlement loop is not implemented and no accounting mutation is performed",
            pool.Id, pool.Engine);
        return Task.CompletedTask;
    }

    public static CreatePayoutReservationRequest BuildReservationRequest(PayoutProcessorPoolConfig pool,
        PayoutProfile profile, PayoutProcessorConfig config)
    {
        return new CreatePayoutReservationRequest
        {
            PoolId = pool.Id,
            Coin = pool.Coin,
            CoinFamily = profile.CoinFamily,
            Handler = profile.AdapterId,
            SendShape = profile.SendShape,
            MinimumPayment = pool.MinimumPayment,
            // ReservationMaxCandidates is a safety page size. Balances outside the page remain eligible for later reservation cycles.
            MaxCandidates = config.ReservationMaxCandidates,
            Created = DateTime.UtcNow,
            RewardRecipientThresholds = pool.RewardRecipients
                .Select(x => new PayoutRewardRecipientThreshold
                {
                    Address = x.Address,
                    MinimumPayment = x.MinimumPayment
                })
                .ToArray()
        };
    }

    public static PayoutPlanningRunnerRequest BuildPlanningRequest(PayoutProcessorPoolConfig pool,
        PayoutProcessorConfig config)
    {
        return new PayoutPlanningRunnerRequest
        {
            PoolId = pool.Id,
            MaxBatches = config.PlanningMaxBatches,
            Created = DateTime.UtcNow
        };
    }

    public static PayoutExecutionRunnerRequest BuildExecutionRequest(PayoutProcessorPoolConfig pool,
        PayoutProcessorConfig config)
    {
        return new PayoutExecutionRunnerRequest
        {
            PoolId = pool.Id,
            MaxAttempts = config.ExecutionBatchSize,
            Started = DateTime.UtcNow
        };
    }

    public static PayoutStaleSendReconciliationRunnerRequest BuildStaleSendReconciliationRequest(
        PayoutProcessorPoolConfig pool, PayoutProcessorConfig config)
    {
        var now = DateTime.UtcNow;
        return new PayoutStaleSendReconciliationRunnerRequest
        {
            PoolId = pool.Id,
            OlderThan = now.AddSeconds(-config.StaleSendingAgeSeconds),
            Updated = now,
            Limit = config.ExecutionBatchSize,
            ErrorCode = StaleSendingErrorCode,
            ErrorMessage = StaleSendingErrorMessage
        };
    }
}
