using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.PayoutProcessor.Configuration;
using HashStormCore.PayoutProcessor.Services;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Payments;
using HashStormCore.Persistence.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace HashStormCore.Tests.PayoutProcessor;

public class PayoutPoolOrchestratorTests
{
    [Fact]
    public void BuildReservationRequestMapsReadyProfileToReservationFields()
    {
        var pool = new PayoutProcessorPoolConfig(
            "pool-a",
            "bitcoin",
            "intent",
            0.25m,
            new[]
            {
                new PayoutProcessorRewardRecipientConfig("reward-address", 1.5m, "operator", 0m),
                new PayoutProcessorRewardRecipientConfig("inherited-address", 0.5m, "foundation", null)
            });
        var profile = new PayoutProfile
        {
            CoinKey = "bitcoin",
            CoinSymbol = "BTC",
            CoinFamily = "bitcoin",
            AdapterId = PayoutProfileConstants.AdapterIds.BitcoinRpc,
            SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
            SendMethod = PayoutProfileConstants.SendMethods.SendMany,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
            ReservationReady = true
        };
        var config = new PayoutProcessorConfig
        {
            ReservationMaxCandidates = 123
        };
        var before = DateTime.UtcNow;

        var request = PayoutPoolOrchestrator.BuildReservationRequest(pool, profile, config);

        var after = DateTime.UtcNow;
        Assert.Equal("pool-a", request.PoolId);
        Assert.Equal("bitcoin", request.Coin);
        Assert.Equal("bitcoin", request.CoinFamily);
        Assert.Equal(PayoutProfileConstants.AdapterIds.BitcoinRpc, request.Handler);
        Assert.Equal(PayoutProfileConstants.SendShapes.BatchMultiRecipient, request.SendShape);
        Assert.Equal(0.25m, request.MinimumPayment);
        Assert.Equal(123, request.MaxCandidates);
        Assert.InRange(request.Created, before, after);
        Assert.Equal(2, request.RewardRecipientThresholds.Count);
        Assert.Contains(request.RewardRecipientThresholds,
            x => x.Address == "reward-address" && x.MinimumPayment == 0m);
        Assert.Contains(request.RewardRecipientThresholds,
            x => x.Address == "inherited-address" && x.MinimumPayment == null);
    }

    [Fact]
    public void BuildReservationRequestUsesAdapterIdNotLegacyHandlerName()
    {
        var pool = new PayoutProcessorPoolConfig("pool-zec-fork", "zec-fork", "intent", 1m,
            Array.Empty<PayoutProcessorRewardRecipientConfig>());
        var profile = new PayoutProfile
        {
            CoinKey = "zec-fork",
            CoinSymbol = "ZF",
            CoinFamily = "equihash",
            AdapterId = PayoutProfileConstants.AdapterIds.EquihashBitcoinRpc,
            SendShape = PayoutProfileConstants.SendShapes.PerAddress,
            SendMethod = PayoutProfileConstants.SendMethods.SendToAddress,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
            ReservationReady = true
        };

        var request = PayoutPoolOrchestrator.BuildReservationRequest(pool, profile, new PayoutProcessorConfig());

        Assert.Equal("equihash", request.CoinFamily);
        Assert.Equal(PayoutProfileConstants.AdapterIds.EquihashBitcoinRpc, request.Handler);
        Assert.Equal(PayoutProfileConstants.SendShapes.PerAddress, request.SendShape);
    }

    [Fact]
    public async Task RunReservationTickAsync_DryRunDoesNotCallRunner()
    {
        var runner = Substitute.For<IPayoutReservationRunner>();
        var resolver = Substitute.For<IPayoutProfileResolver>();
        resolver.Resolve("bitcoin").Returns(PayoutProfileResolution.Resolved(NewReadyProfile()));
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DryRun, resolver, runner);

        await orchestrator.RunReservationTickAsync(NewPool("bitcoin", "intent"), CancellationToken.None);

        await runner.DidNotReceive().CreateReservationAsync(Arg.Any<CreatePayoutReservationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BuildPlanningRequestMapsPoolIdAndConfiguredLimit()
    {
        var pool = NewPool("bitcoin", "intent");
        var config = new PayoutProcessorConfig
        {
            ExecutionBatchSize = 17
        };
        var before = DateTime.UtcNow;

        var request = PayoutPoolOrchestrator.BuildPlanningRequest(pool, config);

        var after = DateTime.UtcNow;
        Assert.Equal("pool-a", request.PoolId);
        Assert.Equal(17, request.MaxBatches);
        Assert.InRange(request.Created, before, after);
    }

    [Fact]
    public async Task RunPlanningTickAsync_DryRunDoesNotCallPlanningRunner()
    {
        var planningRunner = Substitute.For<IPayoutPlanningRunner>();
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DryRun,
            Substitute.For<IPayoutProfileResolver>(),
            Substitute.For<IPayoutReservationRunner>(),
            planningRunner);

        await orchestrator.RunPlanningTickAsync(NewPool("bitcoin", "intent"), CancellationToken.None);

        await planningRunner.DidNotReceive().CreateSendAttemptsAsync(Arg.Any<PayoutPlanningRunnerRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPlanningTickAsync_DisabledConfigSkipsBeforePlanningRunner()
    {
        var planningRunner = Substitute.For<IPayoutPlanningRunner>();
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating,
            Substitute.For<IPayoutProfileResolver>(),
            Substitute.For<IPayoutReservationRunner>(),
            planningRunner,
            enabled: false);

        await orchestrator.RunPlanningTickAsync(NewPool("bitcoin", "intent"), CancellationToken.None);

        await planningRunner.DidNotReceive().CreateSendAttemptsAsync(Arg.Any<PayoutPlanningRunnerRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPlanningTickAsync_DisabledModeSkipsBeforePlanningRunner()
    {
        var planningRunner = Substitute.For<IPayoutPlanningRunner>();
        var orchestrator = NewOrchestrator(PayoutProcessorMode.Disabled,
            Substitute.For<IPayoutProfileResolver>(),
            Substitute.For<IPayoutReservationRunner>(),
            planningRunner);

        await orchestrator.RunPlanningTickAsync(NewPool("bitcoin", "intent"), CancellationToken.None);

        await planningRunner.DidNotReceive().CreateSendAttemptsAsync(Arg.Any<PayoutPlanningRunnerRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPlanningTickAsync_LegacyEngineSkipsBeforePlanningRunner()
    {
        var planningRunner = Substitute.For<IPayoutPlanningRunner>();
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating,
            Substitute.For<IPayoutProfileResolver>(),
            Substitute.For<IPayoutReservationRunner>(),
            planningRunner);

        await orchestrator.RunPlanningTickAsync(NewPool("bitcoin", "legacy"), CancellationToken.None);

        await planningRunner.DidNotReceive().CreateSendAttemptsAsync(Arg.Any<PayoutPlanningRunnerRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPlanningTickAsync_DbMutatingIntentPoolCallsPlanningRunnerWithRequest()
    {
        var planningRunner = Substitute.For<IPayoutPlanningRunner>();
        PayoutPlanningRunnerRequest capturedRequest = null;
        planningRunner.CreateSendAttemptsAsync(Arg.Do<PayoutPlanningRunnerRequest>(x => capturedRequest = x),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PayoutPlanningRunnerResult()));
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating,
            Substitute.For<IPayoutProfileResolver>(),
            Substitute.For<IPayoutReservationRunner>(),
            planningRunner,
            executionBatchSize: 23);

        await orchestrator.RunPlanningTickAsync(NewPool("bitcoin", "intent"), CancellationToken.None);

        await planningRunner.Received(1).CreateSendAttemptsAsync(Arg.Any<PayoutPlanningRunnerRequest>(),
            Arg.Any<CancellationToken>());
        Assert.NotNull(capturedRequest);
        Assert.Equal("pool-a", capturedRequest.PoolId);
        Assert.Equal(23, capturedRequest.MaxBatches);
    }

    [Fact]
    public async Task RunPlanningTickAsync_DbMutatingInvalidBatchSizeSkipsBeforePlanningRunner()
    {
        var planningRunner = Substitute.For<IPayoutPlanningRunner>();
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating,
            Substitute.For<IPayoutProfileResolver>(),
            Substitute.For<IPayoutReservationRunner>(),
            planningRunner,
            executionBatchSize: 0);

        await orchestrator.RunPlanningTickAsync(NewPool("bitcoin", "intent"), CancellationToken.None);

        await planningRunner.DidNotReceive().CreateSendAttemptsAsync(Arg.Any<PayoutPlanningRunnerRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonPlanningTicksDoNotCallPlanningRunner()
    {
        var planningRunner = Substitute.For<IPayoutPlanningRunner>();
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating,
            Substitute.For<IPayoutProfileResolver>(),
            Substitute.For<IPayoutReservationRunner>(),
            planningRunner);
        var pool = NewPool("bitcoin", "intent");

        await orchestrator.RunExecutionTickAsync(pool, CancellationToken.None);
        await orchestrator.RunStaleReconciliationTickAsync(pool, CancellationToken.None);
        await orchestrator.RunOperationIdReconciliationTickAsync(pool, CancellationToken.None);
        await orchestrator.RunSettlementTickAsync(pool, CancellationToken.None);

        await planningRunner.DidNotReceive().CreateSendAttemptsAsync(Arg.Any<PayoutPlanningRunnerRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunReservationTickAsync_DisabledConfigSkipsBeforeProfileResolution()
    {
        var runner = Substitute.For<IPayoutReservationRunner>();
        var resolver = Substitute.For<IPayoutProfileResolver>();
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating, resolver, runner, enabled: false);

        await orchestrator.RunReservationTickAsync(NewPool("bitcoin", "intent"), CancellationToken.None);

        resolver.DidNotReceive().Resolve(Arg.Any<string>());
        await runner.DidNotReceive().CreateReservationAsync(Arg.Any<CreatePayoutReservationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunReservationTickAsync_DisabledModeSkipsBeforeProfileResolution()
    {
        var runner = Substitute.For<IPayoutReservationRunner>();
        var resolver = Substitute.For<IPayoutProfileResolver>();
        var orchestrator = NewOrchestrator(PayoutProcessorMode.Disabled, resolver, runner);

        await orchestrator.RunReservationTickAsync(NewPool("bitcoin", "intent"), CancellationToken.None);

        resolver.DidNotReceive().Resolve(Arg.Any<string>());
        await runner.DidNotReceive().CreateReservationAsync(Arg.Any<CreatePayoutReservationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunReservationTickAsync_SkipsLegacyEngineBeforeProfileResolution()
    {
        var runner = Substitute.For<IPayoutReservationRunner>();
        var resolver = Substitute.For<IPayoutProfileResolver>();
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating, resolver, runner);

        await orchestrator.RunReservationTickAsync(NewPool("bitcoin", "legacy"), CancellationToken.None);

        resolver.DidNotReceive().Resolve(Arg.Any<string>());
        await runner.DidNotReceive().CreateReservationAsync(Arg.Any<CreatePayoutReservationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunReservationTickAsync_UnsupportedProfileDoesNotOpenConnection()
    {
        var runner = Substitute.For<IPayoutReservationRunner>();
        var resolver = Substitute.For<IPayoutProfileResolver>();
        resolver.Resolve("unknown").Returns(PayoutProfileResolution.Unsupported("missing coins.json entry"));
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating, resolver, runner);

        await orchestrator.RunReservationTickAsync(NewPool("unknown", "intent"), CancellationToken.None);

        await runner.DidNotReceive().CreateReservationAsync(Arg.Any<CreatePayoutReservationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunReservationTickAsync_NotReadyProfileDoesNotOpenConnection()
    {
        var runner = Substitute.For<IPayoutReservationRunner>();
        var resolver = Substitute.For<IPayoutProfileResolver>();
        resolver.Resolve("kaspa").Returns(PayoutProfileResolution.Resolved(new PayoutProfile
        {
            CoinKey = "kaspa",
            CoinSymbol = "KAS",
            CoinFamily = "kaspa",
            AdapterId = PayoutProfileConstants.AdapterIds.KaspaWalletWrapper,
            SendShape = PayoutProfileConstants.SendShapes.PerAddress,
            SendMethod = PayoutProfileConstants.SendMethods.KaspaSend,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.UnsafePlaceholder,
            ReservationReady = false,
            NotReadyReason = "placeholder evidence is not settlement-safe"
        }));
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating, resolver, runner);

        await orchestrator.RunReservationTickAsync(NewPool("kaspa", "intent"), CancellationToken.None);

        await runner.DidNotReceive().CreateReservationAsync(Arg.Any<CreatePayoutReservationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunReservationTickAsync_DbMutatingReadyProfileCallsRunnerWithReservationRequest()
    {
        var runner = Substitute.For<IPayoutReservationRunner>();
        var resolver = Substitute.For<IPayoutProfileResolver>();
        var profile = NewReadyProfile();
        var pool = new PayoutProcessorPoolConfig(
            "pool-a",
            "bitcoin",
            "intent",
            0.25m,
            new[]
            {
                new PayoutProcessorRewardRecipientConfig("reward-address", 1.5m, "operator", 0m),
                new PayoutProcessorRewardRecipientConfig("inherited-address", 0.5m, "foundation", null)
            });
        CreatePayoutReservationRequest capturedRequest = null;
        resolver.Resolve("bitcoin").Returns(PayoutProfileResolution.Resolved(profile));
        runner.CreateReservationAsync(Arg.Do<CreatePayoutReservationRequest>(x => capturedRequest = x),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreatePayoutReservationResult.NoEligibleBalances()));
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating, resolver, runner,
            reservationMaxCandidates: 321);

        await orchestrator.RunReservationTickAsync(pool, CancellationToken.None);

        await runner.Received(1).CreateReservationAsync(Arg.Any<CreatePayoutReservationRequest>(),
            Arg.Any<CancellationToken>());
        Assert.NotNull(capturedRequest);
        Assert.Equal(profile.CoinFamily, capturedRequest.CoinFamily);
        Assert.Equal(profile.AdapterId, capturedRequest.Handler);
        Assert.Equal(profile.SendShape, capturedRequest.SendShape);
        Assert.Equal(pool.MinimumPayment, capturedRequest.MinimumPayment);
        Assert.Equal(321, capturedRequest.MaxCandidates);
        Assert.Equal(2, capturedRequest.RewardRecipientThresholds.Count);
        Assert.Contains(capturedRequest.RewardRecipientThresholds,
            x => x.Address == "reward-address" && x.MinimumPayment == 0m);
        Assert.Contains(capturedRequest.RewardRecipientThresholds,
            x => x.Address == "inherited-address" && x.MinimumPayment == null);
    }

    [Fact]
    public async Task RunReservationTickAsync_DbMutatingInvalidMaxCandidatesSkipsBeforeProfileResolution()
    {
        var runner = Substitute.For<IPayoutReservationRunner>();
        var resolver = Substitute.For<IPayoutProfileResolver>();
        var orchestrator = NewOrchestrator(PayoutProcessorMode.DbMutating, resolver, runner,
            reservationMaxCandidates: 0);

        await orchestrator.RunReservationTickAsync(NewPool("bitcoin", "intent"), CancellationToken.None);

        resolver.DidNotReceive().Resolve(Arg.Any<string>());
        await runner.DidNotReceive().CreateReservationAsync(Arg.Any<CreatePayoutReservationRequest>(),
            Arg.Any<CancellationToken>());
    }

    private static PayoutPoolOrchestrator NewOrchestrator(PayoutProcessorMode mode, IPayoutProfileResolver resolver,
        IPayoutReservationRunner runner, IPayoutPlanningRunner planningRunner = null, bool enabled = true,
        int reservationMaxCandidates = 50, int executionBatchSize = 8)
    {
        var config = new PayoutProcessorConfig
        {
            Enabled = enabled,
            Mode = mode,
            ReservationMaxCandidates = reservationMaxCandidates,
            ExecutionBatchSize = executionBatchSize
        };

        return new PayoutPoolOrchestrator(config, resolver, runner,
            planningRunner ?? Substitute.For<IPayoutPlanningRunner>(),
            NullLogger<PayoutPoolOrchestrator>.Instance);
    }

    private static PayoutProcessorPoolConfig NewPool(string coin, string engine)
    {
        return new PayoutProcessorPoolConfig("pool-a", coin, engine, 0.1m,
            Array.Empty<PayoutProcessorRewardRecipientConfig>());
    }

    private static PayoutProfile NewReadyProfile()
    {
        return new PayoutProfile
        {
            CoinKey = "bitcoin",
            CoinSymbol = "BTC",
            CoinFamily = "bitcoin",
            AdapterId = PayoutProfileConstants.AdapterIds.BitcoinRpc,
            SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
            SendMethod = PayoutProfileConstants.SendMethods.SendMany,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
            ReservationReady = true
        };
    }
}
