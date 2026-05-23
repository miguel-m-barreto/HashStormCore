using HashStormCore.PayoutProcessor.Configuration;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using Microsoft.Extensions.Logging;

namespace HashStormCore.PayoutProcessor.Services;

public class PayoutPoolOrchestrator
{
    private const string PayoutEngineIntent = "intent";

    public PayoutPoolOrchestrator(PayoutProcessorConfig config, IPayoutProfileResolver payoutProfileResolver,
        IPayoutReservationRunner payoutReservationRunner, IPayoutPlanningRunner payoutPlanningRunner,
        ILogger<PayoutPoolOrchestrator> logger)
    {
        this.config = config;
        this.payoutProfileResolver = payoutProfileResolver;
        this.payoutReservationRunner = payoutReservationRunner;
        this.payoutPlanningRunner = payoutPlanningRunner;
        this.logger = logger;
    }

    private readonly PayoutProcessorConfig config;
    private readonly IPayoutProfileResolver payoutProfileResolver;
    private readonly IPayoutReservationRunner payoutReservationRunner;
    private readonly IPayoutPlanningRunner payoutPlanningRunner;
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

        if(config.ExecutionBatchSize <= 0)
        {
            logger.LogWarning(
                "Skipping payout planning for pool {PoolId}: ExecutionBatchSize must be greater than zero for DbMutating planning",
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
    }

    public Task RunExecutionTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        logger.LogInformation(
            "Payout execution tick for pool {PoolId} using engine {Engine}: execution loop is not implemented and no sender/RPC/DB mutation is performed",
            pool.Id, pool.Engine);
        return Task.CompletedTask;
    }

    public Task RunStaleReconciliationTickAsync(PayoutProcessorPoolConfig pool, CancellationToken ct)
    {
        logger.LogInformation(
            "Stale sending reconciliation tick for pool {PoolId} using engine {Engine}: reconciliation loop is not implemented and no DB mutation is performed",
            pool.Id, pool.Engine);
        return Task.CompletedTask;
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
            MaxBatches = config.ExecutionBatchSize,
            Created = DateTime.UtcNow
        };
    }
}
