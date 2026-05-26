using System;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Postgres;
using HashStormCore.Persistence.Postgres.Repositories;
using HashStormCore.Persistence.Repositories;
using Npgsql;
using Xunit;

namespace HashStormCore.Tests.Persistence.Postgres;

public class DbPayoutSettlementRunnerTests : PostgresCommittedIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository payoutIntentRepo = new();

    [PostgresIntegrationFact]
    public async Task SettleAcceptedAttemptsAsync_ValidatesRequest()
    {
        var runner = NewRunner(TxIdProfile());
        var now = UtcNow();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.SettleAcceptedAttemptsAsync(NewRequest(" ", settledAt: now), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.SettleAcceptedAttemptsAsync(NewRequest("pool", limit: 0, settledAt: now), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.SettleAcceptedAttemptsAsync(NewRequest("pool", settledAt: default), Ct));
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_PropagatesCancellation()
    {
        var runner = NewRunner(TxIdProfile());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        return Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.SettleAcceptedAttemptsAsync(NewRequest("pool"), cts.Token));
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_EligibleTxIdCandidateSettlesThroughServicePath()
    {
        var poolId = NewCommittedPoolId("settlement_runner_txid");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, poolId, now,
                PayoutExternalConfirmationKinds.TxId, "txid-runner-settle", null, null, ("addr-a", 1m));
            await InsertBalanceAsync(con, poolId, "addr-a", 1m, now);

            var result = await NewRunner(TxIdProfile()).SettleAcceptedAttemptsAsync(
                NewRequest(poolId, settledAt: now.AddMinutes(3)), Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.SettledCount);
            Assert.Equal(0, result.SkippedCount);
            Assert.Equal(PayoutIntentStates.Settled, await GetIntentStateAsync(con, data.Batch.Intents[0].Id));
            Assert.Equal(1, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(1, await CountPoolRowsAsync(con, "balance_changes", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_EligibleRawHashCandidateSettlesThroughServicePath()
    {
        var poolId = NewCommittedPoolId("settlement_runner_rawhash");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, poolId, now,
                PayoutExternalConfirmationKinds.RawHash, "rawhash-runner-settle", null, null, ("addr-a", 1m));
            await InsertBalanceAsync(con, poolId, "addr-a", 1m, now);

            var result = await NewRunner(RawHashProfile()).SettleAcceptedAttemptsAsync(
                NewRequest(poolId, settledAt: now.AddMinutes(3)), Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.SettledCount);
            Assert.Equal(0, result.SkippedCount);
            Assert.Equal(PayoutIntentStates.Settled, await GetIntentStateAsync(con, data.Batch.Intents[0].Id));
            Assert.Equal(1, await CountPoolRowsAsync(con, "payments", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_AsyncOperationProfileSettlesOnlyFinalTxIdEvidence()
    {
        var poolId = NewCommittedPoolId("settlement_runner_async_txid");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, poolId, now,
                PayoutExternalConfirmationKinds.OperationId, "operation-runner-settle",
                PayoutExternalConfirmationKinds.TxId, "txid-from-operation-runner", ("addr-a", 1m));
            await InsertBalanceAsync(con, poolId, "addr-a", 1m, now);

            var result = await NewRunner(AsyncOperationProfile()).SettleAcceptedAttemptsAsync(
                NewRequest(poolId, settledAt: now.AddMinutes(4)), Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.SettledCount);
            Assert.Equal(0, result.SkippedCount);
            Assert.Equal(PayoutIntentStates.Settled, await GetIntentStateAsync(con, data.Batch.Intents[0].Id));
            Assert.Equal("txid-from-operation-runner", await GetIntentConfirmationAsync(con, data.Batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_OperationIdCandidateIsSkippedByProfileValidation()
    {
        var candidate = Candidate(PayoutExternalConfirmationKinds.OperationId, "operation-only");
        var repo = new FakeSettlementRepository(new[] { candidate },
            PayoutSettlementResult.Settled(candidate.BatchId, candidate.AttemptId,
                Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>(), "should-not-settle"));
        var runner = NewRunner(TxIdProfile(), repo);

        return AssertSkippedWithoutRepositorySettlementAsync(runner, repo);
    }

    [PostgresIntegrationFact]
    public async Task SettleAcceptedAttemptsAsync_RepositorySettlementIsReachedThroughSettlementService()
    {
        var candidate = Candidate(PayoutExternalConfirmationKinds.TxId, "txid-service-mediated");
        var repo = new FakeSettlementRepository(new[] { candidate },
            PayoutSettlementResult.Settled(candidate.BatchId, candidate.AttemptId,
                Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>(), candidate.TransactionConfirmationData));
        var runner = NewRunner(TxIdProfile(), repo);

        var result = await runner.SettleAcceptedAttemptsAsync(NewRequest("pool-a"), Ct);

        Assert.Equal(1, result.SettledCount);
        Assert.Equal(1, repo.SettleCallCount);
        Assert.Contains(nameof(PayoutSettlementService), repo.SettleStackTrace);
        Assert.Contains(nameof(PayoutSettlementService.SettleEligibleCandidateAsync), repo.SettleStackTrace);
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_WalletAckCandidateIsSkippedByProfileValidation()
    {
        var candidate = Candidate(PayoutExternalConfirmationKinds.WalletAck, "wallet-ack");
        var repo = new FakeSettlementRepository(new[] { candidate },
            PayoutSettlementResult.Settled(candidate.BatchId, candidate.AttemptId,
                Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>(), "should-not-settle"));
        var runner = NewRunner(TxIdProfile(), repo);

        return AssertSkippedWithoutRepositorySettlementAsync(runner, repo);
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_WrongEvidenceKindIsSkippedAndNotSettled()
    {
        var poolId = NewCommittedPoolId("settlement_runner_wrong_kind");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, poolId, now,
                PayoutExternalConfirmationKinds.RawHash, "rawhash-wrong-profile", null, null, ("addr-a", 1m));
            await InsertBalanceAsync(con, poolId, "addr-a", 1m, now);

            var result = await NewRunner(TxIdProfile()).SettleAcceptedAttemptsAsync(
                NewRequest(poolId, settledAt: now.AddMinutes(3)), Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(0, result.SettledCount);
            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, data.Batch.Intents[0].Id));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_UnsafeEvidenceIsSkippedAndNotSettled()
    {
        var poolId = NewCommittedPoolId("settlement_runner_unsafe");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, poolId, now,
                PayoutExternalConfirmationKinds.TxId, "send:fake-txid", null, null, ("addr-a", 1m));
            await InsertBalanceAsync(con, poolId, "addr-a", 1m, now);

            var result = await NewRunner(TxIdProfile()).SettleAcceptedAttemptsAsync(
                NewRequest(poolId, settledAt: now.AddMinutes(3)), Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(0, result.SettledCount);
            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(PayoutSettlementEligibilityStatus.UnsafeEvidenceValue.ToString(),
                Assert.Single(result.SkippedCandidates).Status);
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, data.Batch.Intents[0].Id));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_PlaceholderEvidenceIsSkippedAndNotSettled()
    {
        var poolId = NewCommittedPoolId("settlement_runner_placeholder");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, poolId, now,
                PayoutExternalConfirmationKinds.TxId, "fake-placeholder-txid", null, null, ("addr-a", 1m));
            await InsertBalanceAsync(con, poolId, "addr-a", 1m, now);

            var result = await NewRunner(TxIdProfile()).SettleAcceptedAttemptsAsync(
                NewRequest(poolId, settledAt: now.AddMinutes(3)), Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(0, result.SettledCount);
            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(PayoutSettlementEligibilityStatus.UnsafeEvidenceValue.ToString(),
                Assert.Single(result.SkippedCandidates).Status);
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, data.Batch.Intents[0].Id));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
        });
    }

    [PostgresIntegrationFact]
    public async Task SettleAcceptedAttemptsAsync_AlreadySettledResultIsHandledIdempotently()
    {
        var candidate = Candidate(PayoutExternalConfirmationKinds.TxId, "txid-already-settled");
        var repo = new FakeSettlementRepository(new[] { candidate },
            PayoutSettlementResult.AlreadySettled(candidate.BatchId, candidate.AttemptId,
                new[] { 30L }, new[] { 40L }, new[] { 50L }, "txid-already-settled"));
        var runner = NewRunner(TxIdProfile(), repo);

        var result = await runner.SettleAcceptedAttemptsAsync(NewRequest("pool-a"), Ct);

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.SettledCount);
        Assert.Equal(1, result.AlreadySettledCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(1, repo.SettleCallCount);
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_ContinuesAfterSkippedCandidateAndSettlesNextEligibleCandidate()
    {
        var poolId = NewCommittedPoolId("settlement_runner_continue");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var skipped = await CreateAcceptedAttemptAsync(con, poolId, now,
                PayoutExternalConfirmationKinds.RawHash, "rawhash-skip-in-runner", null, null, ("addr-a", 1m));
            var settled = await CreateAcceptedAttemptAsync(con, poolId, now.AddSeconds(1),
                PayoutExternalConfirmationKinds.TxId, "txid-settle-after-skip", null, null, ("addr-b", 1m));
            await InsertBalanceAsync(con, poolId, "addr-a", 1m, now);
            await InsertBalanceAsync(con, poolId, "addr-b", 1m, now);

            var result = await NewRunner(TxIdProfile()).SettleAcceptedAttemptsAsync(
                NewRequest(poolId, limit: 10, settledAt: now.AddMinutes(3)), Ct);

            Assert.Equal(2, result.CandidateCount);
            Assert.Equal(1, result.SettledCount);
            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, skipped.Batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.Settled, await GetIntentStateAsync(con, settled.Batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptsAsync_RollsBackFailedCandidateAccountingAndContinues()
    {
        var poolId = NewCommittedPoolId("settlement_runner_atomic_failure");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var failed = await CreateAcceptedAttemptAsync(con, poolId, now,
                PayoutExternalConfirmationKinds.TxId, "txid-partial-failure", null, null,
                ("addr-failed", 1m));
            var settled = await CreateAcceptedAttemptAsync(con, poolId, now.AddSeconds(1),
                PayoutExternalConfirmationKinds.TxId, "txid-after-partial-failure", null, null,
                ("addr-success", 1m));
            await InsertBalanceAsync(con, poolId, "addr-failed", 1m, now);
            await InsertBalanceAsync(con, poolId, "addr-success", 1m, now);

            var repo = new PartialFailureThenDelegateSettlementRepository(new[]
            {
                CandidateFromData(failed, PayoutExternalConfirmationKinds.TxId, "txid-partial-failure"),
                CandidateFromData(settled, PayoutExternalConfirmationKinds.TxId, "txid-after-partial-failure")
            }, new PayoutSettlementRepository(), failed.Attempt.Id);
            var result = await NewRunner(TxIdProfile(), repo).SettleAcceptedAttemptsAsync(
                NewRequest(poolId, limit: 10, settledAt: now.AddMinutes(3)), Ct);

            Assert.Equal(2, result.CandidateCount);
            Assert.Equal(1, result.FailureCount);
            Assert.Equal(1, result.SettledCount);
            Assert.Equal(0, await CountPoolAddressRowsAsync(con, "payments", poolId, "addr-failed"));
            Assert.Equal(0, await CountPoolAddressRowsAsync(con, "balance_changes", poolId, "addr-failed"));
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, failed.Batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.Settled, await GetIntentStateAsync(con, settled.Batch.Intents[0].Id));
            Assert.Equal(1, await CountPoolAddressRowsAsync(con, "payments", poolId, "addr-success"));
            Assert.Equal(1, await CountPoolAddressRowsAsync(con, "balance_changes", poolId, "addr-success"));
        });
    }

    private async Task AssertSkippedWithoutRepositorySettlementAsync(DbPayoutSettlementRunner runner,
        FakeSettlementRepository repo)
    {
        var result = await runner.SettleAcceptedAttemptsAsync(NewRequest("pool-a"), Ct);

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.SettledCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, repo.SettleCallCount);
        Assert.Equal(PayoutSettlementEligibilityStatus.EvidenceKindNotSupported.ToString(),
            Assert.Single(result.SkippedCandidates).Status);
    }

    private DbPayoutSettlementRunner NewRunner(PayoutProfile profile, IPayoutSettlementRepository repository = null)
    {
        repository ??= new PayoutSettlementRepository();
        var cf = new PgConnectionFactory(GetConnectionString());
        return new DbPayoutSettlementRunner(cf, repository,
            new PayoutSettlementService(repository, new FixedProfileResolver(profile)));
    }

    private static PayoutSettlementRunnerRequest NewRequest(string poolId, int limit = 10, DateTime? settledAt = null)
    {
        return new PayoutSettlementRunnerRequest
        {
            PoolId = poolId,
            Limit = limit,
            SettledAt = settledAt ?? UtcNow()
        };
    }

    private async Task<TestPayoutData> CreateAcceptedAttemptAsync(NpgsqlConnection con, string poolId,
        DateTime created, string acceptedEvidenceKind, string acceptedEvidenceValue, string finalEvidenceKind,
        string finalEvidenceValue, params (string address, decimal amount)[] intents)
    {
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            var batch = await CreateBatchAsync(con, tx, poolId, created, intents);
            var attempt = await CreateAttemptAsync(con, tx, batch, created,
                batch.Intents.Select(x => x.Id).ToArray());
            await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, created.AddMinutes(1), Ct);
            await payoutIntentRepo.MarkAttemptAcceptedAsync(con, tx, attempt.Id, poolId,
                NewEvidence(acceptedEvidenceKind, acceptedEvidenceValue), created.AddMinutes(2), Ct);

            if(!string.IsNullOrWhiteSpace(finalEvidenceKind))
            {
                await payoutIntentRepo.InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
                {
                    PoolId = batch.PoolId,
                    Coin = batch.Coin,
                    BatchId = batch.Id,
                    AttemptId = attempt.Id,
                    Kind = finalEvidenceKind,
                    Value = finalEvidenceValue,
                    Created = created.AddMinutes(3)
                }, Ct);
            }

            await tx.CommitAsync();
            return new TestPayoutData(batch, attempt);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private Task<PayoutBatch> CreateBatchAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId,
        DateTime created, params (string address, decimal amount)[] intents)
    {
        var intentRequests = intents.Select(x => new CreatePayoutIntentRequest
        {
            Address = x.address,
            Amount = x.amount,
            BalanceSnapshotAmount = x.amount,
            BalanceSnapshotUpdated = created,
            PaymentThreshold = 0m
        }).ToArray();

        return payoutIntentRepo.CreateReservedBatchAsync(con, tx, new CreatePayoutBatchRequest
        {
            PoolId = poolId,
            Coin = Coin,
            CoinFamily = CoinFamily,
            Handler = Handler,
            SendShape = PayoutSendShapes.BatchMultiRecipient,
            RecipientSetHash = $"settlement-runner-recipient-set-{Guid.NewGuid():N}",
            MinimumAmount = 0m,
            ReservedAmountSnapshot = intents.Sum(x => x.amount),
            IntentCountSnapshot = intents.Length,
            Created = created
        }, intentRequests, Ct);
    }

    private Task<PayoutSendAttempt> CreateAttemptAsync(NpgsqlConnection con, NpgsqlTransaction tx, PayoutBatch batch,
        DateTime created, params long[] intentIds)
    {
        var selectedIntents = batch.Intents.Where(x => intentIds.Contains(x.Id)).ToArray();

        return payoutIntentRepo.CreateSendAttemptAsync(con, tx, new CreatePayoutSendAttemptRequest
        {
            BatchId = batch.Id,
            PoolId = batch.PoolId,
            Coin = batch.Coin,
            AttemptNo = 1,
            Method = Method,
            RequestHash = $"settlement-runner-request-{Guid.NewGuid():N}",
            RequestSummary = $"test:recipients={selectedIntents.Length}",
            RecipientCount = selectedIntents.Length,
            AmountSnapshot = selectedIntents.Sum(x => x.Amount),
            Created = created
        }, intentIds, Ct);
    }

    private static Task InsertBalanceAsync(NpgsqlConnection con, string poolId, string address, decimal amount,
        DateTime created)
    {
        return con.ExecuteAsync(@"INSERT INTO balances(poolid, address, amount, created, updated)
            VALUES(@poolid, @address, @amount, @created, @created)",
            new { poolid = poolId, address, amount, created });
    }

    private static Task<string> GetIntentStateAsync(NpgsqlConnection con, long intentId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_intents WHERE id = @intentid",
            new { intentid = intentId });
    }

    private static Task<string> GetIntentConfirmationAsync(NpgsqlConnection con, long intentId)
    {
        return con.QuerySingleAsync<string>("SELECT transactionconfirmationdata FROM payout_intents WHERE id = @intentid",
            new { intentid = intentId });
    }

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid", new { poolid = poolId });
    }

    private static Task<int> CountPoolAddressRowsAsync(NpgsqlConnection con, string table, string poolId,
        string address)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid AND address = @address",
            new { poolid = poolId, address });
    }

    private static PayoutAttemptEvidence NewEvidence(string kind, string value)
    {
        return new PayoutAttemptEvidence
        {
            Kind = kind,
            Value = value
        };
    }

    private static PayoutSettlementAttemptCandidate Candidate(string evidenceKind, string evidenceValue)
    {
        return new PayoutSettlementAttemptCandidate
        {
            BatchId = 10,
            AttemptId = 20,
            PoolId = "pool-a",
            Coin = Coin,
            Method = Method,
            EvidenceKind = evidenceKind,
            TransactionConfirmationData = evidenceValue
        };
    }

    private static PayoutSettlementAttemptCandidate CandidateFromData(TestPayoutData data, string evidenceKind,
        string evidenceValue)
    {
        return new PayoutSettlementAttemptCandidate
        {
            BatchId = data.Batch.Id,
            AttemptId = data.Attempt.Id,
            PoolId = data.Batch.PoolId,
            Coin = data.Batch.Coin,
            Method = data.Attempt.Method,
            EvidenceKind = evidenceKind,
            TransactionConfirmationData = evidenceValue
        };
    }

    private static PayoutProfile TxIdProfile()
    {
        return new PayoutProfile
        {
            CoinKey = Coin,
            CoinFamily = CoinFamily,
            AdapterId = Handler,
            SendShape = PayoutSendShapes.BatchMultiRecipient,
            SendMethod = Method,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
            ReservationReady = true
        };
    }

    private static PayoutProfile RawHashProfile()
    {
        return TxIdProfile() with
        {
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.RawHash
        };
    }

    private static PayoutProfile AsyncOperationProfile()
    {
        return TxIdProfile() with
        {
            SendShape = PayoutProfileConstants.SendShapes.AsyncOperation,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId,
            RequiresOperationIdProvider = true,
            SupportsShieldedOperationTracking = true
        };
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private sealed record TestPayoutData(PayoutBatch Batch, PayoutSendAttempt Attempt);

    private sealed class FixedProfileResolver : IPayoutProfileResolver
    {
        public FixedProfileResolver(PayoutProfile profile)
        {
            this.profile = profile;
        }

        private readonly PayoutProfile profile;

        public PayoutProfileResolution Resolve(string coinKey)
        {
            return PayoutProfileResolution.Resolved(profile);
        }
    }

    private sealed class FakeSettlementRepository : IPayoutSettlementRepository
    {
        public FakeSettlementRepository(PayoutSettlementAttemptCandidate[] candidates, PayoutSettlementResult result)
        {
            this.candidates = candidates;
            this.result = result;
        }

        private readonly PayoutSettlementAttemptCandidate[] candidates;
        private readonly PayoutSettlementResult result;

        public int SettleCallCount { get; private set; }
        public string SettleStackTrace { get; private set; }

        public Task<PayoutSettlementAttemptCandidate[]> GetAcceptedAttemptsForSettlementAsync(IDbConnection con,
            IDbTransaction tx, string poolId, int limit, CancellationToken ct)
        {
            return Task.FromResult(candidates);
        }

        public Task<PayoutSettlementResult> SettleAcceptedAttemptAsync(IDbConnection con, IDbTransaction tx,
            PayoutSettlementRequest request, CancellationToken ct)
        {
            SettleCallCount++;
            SettleStackTrace = new StackTrace().ToString();
            return Task.FromResult(result);
        }
    }

    private sealed class PartialFailureThenDelegateSettlementRepository : IPayoutSettlementRepository
    {
        public PartialFailureThenDelegateSettlementRepository(PayoutSettlementAttemptCandidate[] candidates,
            IPayoutSettlementRepository inner, long failingAttemptId)
        {
            this.candidates = candidates;
            this.inner = inner;
            this.failingAttemptId = failingAttemptId;
        }

        private readonly PayoutSettlementAttemptCandidate[] candidates;
        private readonly IPayoutSettlementRepository inner;
        private readonly long failingAttemptId;

        public Task<PayoutSettlementAttemptCandidate[]> GetAcceptedAttemptsForSettlementAsync(IDbConnection con,
            IDbTransaction tx, string poolId, int limit, CancellationToken ct)
        {
            return Task.FromResult(candidates);
        }

        public async Task<PayoutSettlementResult> SettleAcceptedAttemptAsync(IDbConnection con, IDbTransaction tx,
            PayoutSettlementRequest request, CancellationToken ct)
        {
            if(request.AttemptId != failingAttemptId)
                return await inner.SettleAcceptedAttemptAsync(con, tx, request, ct);

            await con.ExecuteAsync(new CommandDefinition(@"INSERT INTO payments(
                    poolid, coin, address, amount, transactionconfirmationdata, created)
                VALUES(@poolid, @coin, @address, @amount, @transactionconfirmationdata, @created)",
                new
                {
                    poolid = request.PoolId,
                    coin = Coin,
                    address = "addr-failed",
                    amount = 1m,
                    transactionconfirmationdata = "txid-partial-failure",
                    created = request.SettledAt
                }, tx, cancellationToken: ct));

            await con.ExecuteAsync(new CommandDefinition(@"INSERT INTO balance_changes(
                    poolid, address, amount, usage, tags, created)
                VALUES(@poolid, @address, @amount, @usage, NULL, @created)",
                new
                {
                    poolid = request.PoolId,
                    address = "addr-failed",
                    amount = -1m,
                    usage = "test partial settlement failure",
                    created = request.SettledAt
                }, tx, cancellationToken: ct));

            throw new InvalidOperationException("Simulated settlement failure after partial accounting write");
        }
    }
}
