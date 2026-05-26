using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.PayoutProcessor.Configuration;
using HashStormCore.PayoutProcessor.Services;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Payments;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;
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
            PlanningMaxBatches = 17,
            ExecutionBatchSize = 99
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
            planningMaxBatches: 23,
            executionBatchSize: 99);

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
            planningMaxBatches: 0);

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
    public async Task DbPayoutPlanningRunnerSkipsCoinFamilyMismatchWithStructuredReason()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var con = Substitute.For<IDbConnection>();
        var tx = Substitute.For<IDbTransaction>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(con));
        con.BeginTransaction(IsolationLevel.ReadCommitted).Returns(tx);

        var intentRepo = Substitute.For<IPayoutIntentRepository>();
        intentRepo.GetReservedBatchesForPlanningAsync(con, tx, "pool-a", 5, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PayoutPlanningBatchCandidate
                {
                    BatchId = 42,
                    PoolId = "pool-a",
                    Coin = "bitcoin",
                    CoinFamily = "wrong-family",
                    Handler = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                    SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient
                }
            });
        var resolver = Substitute.For<IPayoutProfileResolver>();
        resolver.Resolve("bitcoin").Returns(PayoutProfileResolution.Resolved(NewReadyProfile()));
        var runner = new DbPayoutPlanningRunner(connectionFactory, intentRepo,
            new PayoutSendAttemptPlannerService(intentRepo), resolver);

        var result = await runner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
        {
            PoolId = "pool-a",
            MaxBatches = 5,
            Created = DateTime.UtcNow
        }, CancellationToken.None);

        Assert.Equal(1, result.CandidateBatchCount);
        Assert.Equal(0, result.PlannedBatchCount);
        Assert.Equal(1, result.SkippedBatchCount);
        var skipped = Assert.Single(result.SkippedBatches);
        Assert.Equal(42, skipped.BatchId);
        Assert.Equal("pool-a", skipped.PoolId);
        Assert.Equal("bitcoin", skipped.Coin);
        Assert.Equal("wrong-family", skipped.CoinFamily);
        Assert.Contains("coinFamily mismatch", skipped.Reason);
    }

    [Fact]
    public async Task DbPayoutPlanningRunnerPassesProfilePlanningPolicyAndIntegratedPrefixesToPlanner()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var con = Substitute.For<IDbConnection>();
        var tx = Substitute.For<IDbTransaction>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(con));
        con.BeginTransaction(IsolationLevel.ReadCommitted).Returns(tx);

        const ulong integratedPrefix = 31444;
        var integratedAddress = CreateCryptoNoteAddress(integratedPrefix, payloadLength: 76);
        var intentRepo = Substitute.For<IPayoutIntentRepository>();
        intentRepo.GetReservedBatchesForPlanningAsync(con, tx, "pool-a", 5, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PayoutPlanningBatchCandidate
                {
                    BatchId = 42,
                    PoolId = "pool-a",
                    Coin = "cryptonote",
                    CoinFamily = "cryptonote",
                    Handler = PayoutProfileConstants.AdapterIds.CryptonoteWalletRpc,
                    SendShape = PayoutProfileConstants.SendShapes.AddressGroup
                }
            });
        intentRepo.GetBatchForUpdateAsync(con, tx, 42, "pool-a", "cryptonote", Arg.Any<CancellationToken>())
            .Returns(new PayoutBatch
            {
                Id = 42,
                PoolId = "pool-a",
                Coin = "cryptonote",
                State = PayoutBatchStates.Reserved,
                SendShape = PayoutProfileConstants.SendShapes.AddressGroup
            });
        intentRepo.GetSendAttemptCountForBatchAsync(con, tx, 42, "pool-a", "cryptonote",
                Arg.Any<CancellationToken>())
            .Returns(0);
        intentRepo.GetReservedIntentsForBatchAsync(con, tx, 42, "pool-a", "cryptonote",
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PayoutIntent
                {
                    Id = 7,
                    BatchId = 42,
                    PoolId = "pool-a",
                    Coin = "cryptonote",
                    Address = integratedAddress,
                    Amount = 1.25m
                }
            });
        CreatePayoutSendAttemptRequest capturedAttempt = null;
        IReadOnlyCollection<long> capturedIntentIds = null;
        intentRepo.CreateSendAttemptAsync(con, tx,
                Arg.Do<CreatePayoutSendAttemptRequest>(x => capturedAttempt = x),
                Arg.Do<IReadOnlyCollection<long>>(x => capturedIntentIds = x),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var attempt = callInfo.Arg<CreatePayoutSendAttemptRequest>();
                return Task.FromResult(new PayoutSendAttempt
                {
                    Id = 100,
                    BatchId = attempt.BatchId,
                    PoolId = attempt.PoolId,
                    Coin = attempt.Coin,
                    Method = attempt.Method,
                    AttemptNo = attempt.AttemptNo,
                    RecipientCount = attempt.RecipientCount,
                    AmountSnapshot = attempt.AmountSnapshot
                });
            });

        var resolver = Substitute.For<IPayoutProfileResolver>();
        resolver.Resolve("cryptonote").Returns(PayoutProfileResolution.Resolved(new PayoutProfile
        {
            CoinKey = "cryptonote",
            CoinSymbol = "XMR",
            CoinFamily = "cryptonote",
            AdapterId = PayoutProfileConstants.AdapterIds.CryptonoteWalletRpc,
            SendShape = PayoutProfileConstants.SendShapes.AddressGroup,
            SendMethod = PayoutProfileConstants.SendMethods.Transfer,
            AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.CryptonotePaymentIdAware,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.RawHash,
            MaxRecipientsPerAttempt = 15,
            IntegratedAddressPrefixes = new[] { integratedPrefix },
            ReservationReady = true
        }));
        var runner = new DbPayoutPlanningRunner(connectionFactory, intentRepo,
            new PayoutSendAttemptPlannerService(intentRepo), resolver);

        var result = await runner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
        {
            PoolId = "pool-a",
            MaxBatches = 5,
            Created = DateTime.UtcNow
        }, CancellationToken.None);

        Assert.Equal(1, result.PlannedBatchCount);
        Assert.NotNull(capturedAttempt);
        Assert.Equal(PayoutProfileConstants.SendMethods.Transfer, capturedAttempt.Method);
        Assert.Equal(1, capturedAttempt.RecipientCount);
        Assert.Equal(new[] { 7L }, capturedIntentIds);
    }

    [Fact]
    public async Task DbPayoutPlanningRunnerPassesAlephiumGroupProfileFieldsToPlannerAndClassifiesAddress()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var con = Substitute.For<IDbConnection>();
        var tx = Substitute.For<IDbTransaction>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(con));
        con.BeginTransaction(IsolationLevel.ReadCommitted).Returns(tx);

        // Official P2PKH fixture — group 1 with AddressGroupCount=4.
        const string group1Address = "1H7CmpbvGJwgyLzR91wzSJJSkiBC92WDPTWny4gmhQJQc";

        var intentRepo = Substitute.For<IPayoutIntentRepository>();
        intentRepo.GetReservedBatchesForPlanningAsync(con, tx, "pool-alph", 5, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PayoutPlanningBatchCandidate
                {
                    BatchId = 77,
                    PoolId = "pool-alph",
                    Coin = "alephium",
                    CoinFamily = "alephium",
                    Handler = PayoutProfileConstants.AdapterIds.AlephiumWalletApi,
                    SendShape = PayoutProfileConstants.SendShapes.AddressGroup
                }
            });
        intentRepo.GetBatchForUpdateAsync(con, tx, 77, "pool-alph", "alephium", Arg.Any<CancellationToken>())
            .Returns(new PayoutBatch
            {
                Id = 77,
                PoolId = "pool-alph",
                Coin = "alephium",
                State = PayoutBatchStates.Reserved,
                SendShape = PayoutProfileConstants.SendShapes.AddressGroup
            });
        intentRepo.GetSendAttemptCountForBatchAsync(con, tx, 77, "pool-alph", "alephium",
                Arg.Any<CancellationToken>())
            .Returns(0);
        intentRepo.GetReservedIntentsForBatchAsync(con, tx, 77, "pool-alph", "alephium",
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PayoutIntent
                {
                    Id = 55,
                    BatchId = 77,
                    PoolId = "pool-alph",
                    Coin = "alephium",
                    Address = group1Address,
                    Amount = 10m
                }
            });
        intentRepo.CreateSendAttemptAsync(con, tx,
                Arg.Any<CreatePayoutSendAttemptRequest>(),
                Arg.Any<IReadOnlyCollection<long>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var attempt = callInfo.Arg<CreatePayoutSendAttemptRequest>();
                return Task.FromResult(new PayoutSendAttempt
                {
                    Id = 200,
                    BatchId = attempt.BatchId,
                    PoolId = attempt.PoolId,
                    Coin = attempt.Coin,
                    Method = attempt.Method,
                    AttemptNo = attempt.AttemptNo,
                    RecipientCount = attempt.RecipientCount,
                    AmountSnapshot = attempt.AmountSnapshot
                });
            });

        var resolver = Substitute.For<IPayoutProfileResolver>();
        resolver.Resolve("alephium").Returns(PayoutProfileResolution.Resolved(new PayoutProfile
        {
            CoinKey = "alephium",
            CoinSymbol = "ALPH",
            CoinFamily = "alephium",
            AdapterId = PayoutProfileConstants.AdapterIds.AlephiumWalletApi,
            SendShape = PayoutProfileConstants.SendShapes.AddressGroup,
            SendMethod = PayoutProfileConstants.SendMethods.BuildSignSubmit,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
            AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.AlephiumGroupAware,
            MaxRecipientsPerAttempt = 64,
            AddressGroupCount = 4,
            ReservationReady = true
        }));
        var runner = new DbPayoutPlanningRunner(connectionFactory, intentRepo,
            new PayoutSendAttemptPlannerService(intentRepo), resolver);

        var result = await runner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
        {
            PoolId = "pool-alph",
            MaxBatches = 5,
            Created = DateTime.UtcNow
        }, CancellationToken.None);

        Assert.Equal(1, result.PlannedBatchCount);
        Assert.Equal(0, result.SkippedBatchCount);
        var planResult = Assert.Single(result.Results);
        Assert.Equal(PayoutSendAttemptPlanningStatus.Created, planResult.Status);
        var attempt = Assert.Single(planResult.Attempts);
        Assert.Equal(PayoutProfileConstants.SendMethods.BuildSignSubmit, attempt.Method);
        Assert.Equal(1, attempt.RecipientCount);
    }

    [Fact]
    public async Task DbPayoutPlanningRunnerPlansKaspaPerAddressSingletonAttempts()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var con = Substitute.For<IDbConnection>();
        var tx = Substitute.For<IDbTransaction>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(con));
        con.BeginTransaction(IsolationLevel.ReadCommitted).Returns(tx);

        var intentRepo = Substitute.For<IPayoutIntentRepository>();
        intentRepo.GetReservedBatchesForPlanningAsync(con, tx, "pool-kas", 5, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PayoutPlanningBatchCandidate
                {
                    BatchId = 88,
                    PoolId = "pool-kas",
                    Coin = "kaspa",
                    CoinFamily = "kaspa",
                    Handler = PayoutProfileConstants.AdapterIds.KaspaWalletWrapper,
                    SendShape = PayoutProfileConstants.SendShapes.PerAddress
                }
            });
        intentRepo.GetBatchForUpdateAsync(con, tx, 88, "pool-kas", "kaspa", Arg.Any<CancellationToken>())
            .Returns(new PayoutBatch
            {
                Id = 88,
                PoolId = "pool-kas",
                Coin = "kaspa",
                State = PayoutBatchStates.Reserved,
                SendShape = PayoutProfileConstants.SendShapes.PerAddress
            });
        intentRepo.GetSendAttemptCountForBatchAsync(con, tx, 88, "pool-kas", "kaspa",
                Arg.Any<CancellationToken>())
            .Returns(0);
        intentRepo.GetReservedIntentsForBatchAsync(con, tx, 88, "pool-kas", "kaspa",
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PayoutIntent
                {
                    Id = 9,
                    BatchId = 88,
                    PoolId = "pool-kas",
                    Coin = "kaspa",
                    Address = "kaspa:qqb",
                    Amount = 1.5m
                },
                new PayoutIntent
                {
                    Id = 8,
                    BatchId = 88,
                    PoolId = "pool-kas",
                    Coin = "kaspa",
                    Address = "kaspa:qqa",
                    Amount = 2.5m
                }
            });

        var capturedAttempts = new List<CreatePayoutSendAttemptRequest>();
        var capturedIntentIds = new List<IReadOnlyCollection<long>>();
        intentRepo.CreateSendAttemptAsync(con, tx,
                Arg.Do<CreatePayoutSendAttemptRequest>(x => capturedAttempts.Add(x)),
                Arg.Do<IReadOnlyCollection<long>>(x => capturedIntentIds.Add(x)),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var attempt = callInfo.Arg<CreatePayoutSendAttemptRequest>();
                return Task.FromResult(new PayoutSendAttempt
                {
                    Id = 300 + attempt.AttemptNo,
                    BatchId = attempt.BatchId,
                    PoolId = attempt.PoolId,
                    Coin = attempt.Coin,
                    Method = attempt.Method,
                    AttemptNo = attempt.AttemptNo,
                    RecipientCount = attempt.RecipientCount,
                    AmountSnapshot = attempt.AmountSnapshot
                });
            });

        var resolver = Substitute.For<IPayoutProfileResolver>();
        resolver.Resolve("kaspa").Returns(PayoutProfileResolution.Resolved(new PayoutProfile
        {
            CoinKey = "kaspa",
            CoinSymbol = "KAS",
            CoinFamily = "kaspa",
            AdapterId = PayoutProfileConstants.AdapterIds.KaspaWalletWrapper,
            SendShape = PayoutProfileConstants.SendShapes.PerAddress,
            SendMethod = PayoutProfileConstants.SendMethods.KaspaSend,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
            AllowsPerAddress = true,
            RequiresExternalWalletWrapper = true,
            ReservationReady = true
        }));
        var runner = new DbPayoutPlanningRunner(connectionFactory, intentRepo,
            new PayoutSendAttemptPlannerService(intentRepo), resolver);

        var result = await runner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
        {
            PoolId = "pool-kas",
            MaxBatches = 5,
            Created = DateTime.UtcNow
        }, CancellationToken.None);

        Assert.Equal(1, result.PlannedBatchCount);
        Assert.Equal(0, result.SkippedBatchCount);
        Assert.Equal(2, capturedAttempts.Count);
        Assert.All(capturedAttempts, x =>
        {
            Assert.Equal(PayoutProfileConstants.SendMethods.KaspaSend, x.Method);
            Assert.Equal(1, x.RecipientCount);
        });
        Assert.Equal(new[] { 8L }, capturedIntentIds.ElementAt(0));
        Assert.Equal(new[] { 9L }, capturedIntentIds.ElementAt(1));
        Assert.All(result.Results.SelectMany(x => x.Attempts), x => Assert.Equal(1, x.RecipientCount));
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
        int reservationMaxCandidates = 50, int planningMaxBatches = 8, int executionBatchSize = 8)
    {
        var config = new PayoutProcessorConfig
        {
            Enabled = enabled,
            Mode = mode,
            ReservationMaxCandidates = reservationMaxCandidates,
            PlanningMaxBatches = planningMaxBatches,
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

    private static string CreateCryptoNoteAddress(ulong prefix, int payloadLength)
    {
        var prefixBytes = EncodeVarInt(prefix);
        var decoded = new byte[prefixBytes.Length + payloadLength];
        Buffer.BlockCopy(prefixBytes, 0, decoded, 0, prefixBytes.Length);

        for(var i = 0; i < payloadLength; i++)
            decoded[prefixBytes.Length + i] = (byte) (i + 1);

        return EncodeCryptoNoteBase58(decoded);
    }

    private static byte[] EncodeVarInt(ulong value)
    {
        var bytes = new List<byte>();

        while(value >= 0x80)
        {
            bytes.Add((byte) ((value & 0x7f) | 0x80));
            value >>= 7;
        }

        bytes.Add((byte) value);
        return bytes.ToArray();
    }

    private static string EncodeCryptoNoteBase58(byte[] bytes)
    {
        var builder = new StringBuilder();
        var offset = 0;

        while(bytes.Length - offset >= 8)
        {
            builder.Append(EncodeCryptoNoteBase58Block(bytes.AsSpan(offset, 8), encodedSize: 11));
            offset += 8;
        }

        var remaining = bytes.Length - offset;
        if(remaining > 0)
            builder.Append(EncodeCryptoNoteBase58Block(bytes.AsSpan(offset, remaining), EncodedBlockSize(remaining)));

        return builder.ToString();
    }

    private static string EncodeCryptoNoteBase58Block(ReadOnlySpan<byte> bytes, int encodedSize)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        ulong value = 0;

        foreach(var b in bytes)
            value = (value << 8) | b;

        var chars = new char[encodedSize];
        Array.Fill(chars, alphabet[0]);

        for(var i = encodedSize - 1; i >= 0 && value > 0; i--)
        {
            chars[i] = alphabet[(int) (value % 58)];
            value /= 58;
        }

        return new string(chars);
    }

    private static int EncodedBlockSize(int decodedSize)
    {
        return decodedSize switch
        {
            1 => 2,
            2 => 3,
            3 => 5,
            4 => 6,
            5 => 7,
            6 => 9,
            7 => 10,
            8 => 11,
            _ => throw new ArgumentOutOfRangeException(nameof(decodedSize))
        };
    }
}
