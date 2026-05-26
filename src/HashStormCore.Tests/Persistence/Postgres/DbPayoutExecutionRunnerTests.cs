using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Postgres;
using HashStormCore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace HashStormCore.Tests.Persistence.Postgres;

public class DbPayoutExecutionRunnerTests : PostgresCommittedIntegrationTestBase
{
    private const string PoolCoin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository payoutIntentRepo = new();

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_MissingPoolIdThrows()
    {
        var runner = NewRunner(new DictionaryProfileResolver(), EmptyRegistry());

        return Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ExecutePreparedAttemptsAsync(NewRequest(" "), Ct));
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_MaxAttemptsMustBePositive()
    {
        var runner = NewRunner(new DictionaryProfileResolver(), EmptyRegistry());

        return Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.ExecutePreparedAttemptsAsync(NewRequest("pool", maxAttempts: 0), Ct));
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_ProfileUnsupportedSkipsPreparedAttemptWithoutSenderCall()
    {
        var poolId = NewCommittedPoolId("exec_runner_unsupported_profile");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreatePreparedAttemptAsync(con, poolId, now, MatchingProfile(), ("addr-a", 1m));
            var sender = AcceptedSender("txid-should-not-send");
            var runner = NewRunner(new DictionaryProfileResolver(), RegistryFor(MatchingProfile(), sender));

            var result = await runner.ExecutePreparedAttemptsAsync(NewRequest(poolId, started: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.CandidateAttemptCount);
            Assert.Equal(1, result.SkippedAttemptCount);
            Assert.Equal(0, result.ExecutedAttemptCount);
            Assert.Equal(0, sender.CallCount);
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, data.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_ProfileNotReadySkipsPreparedAttemptWithoutSenderCall()
    {
        var poolId = NewCommittedPoolId("exec_runner_profile_not_ready");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingProfile() with { ReservationReady = false, NotReadyReason = "not ready" };
            var data = await CreatePreparedAttemptAsync(con, poolId, now, MatchingProfile(), ("addr-a", 1m));
            var sender = AcceptedSender("txid-should-not-send");
            var runner = NewRunner(new DictionaryProfileResolver(profile), RegistryFor(MatchingProfile(), sender));

            var result = await runner.ExecutePreparedAttemptsAsync(NewRequest(poolId, started: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.SkippedAttemptCount);
            Assert.Equal(0, result.ExecutedAttemptCount);
            Assert.Equal(0, sender.CallCount);
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, data.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_NoSenderRegisteredSkipsPreparedAttemptWithoutMutation()
    {
        var poolId = NewCommittedPoolId("exec_runner_no_sender");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingProfile();
            var data = await CreatePreparedAttemptAsync(con, poolId, now, profile, ("addr-a", 1m));
            var runner = NewRunner(new DictionaryProfileResolver(profile), EmptyRegistry());

            var result = await runner.ExecutePreparedAttemptsAsync(NewRequest(poolId, started: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.SkippedAttemptCount);
            Assert.Equal(0, result.ExecutedAttemptCount);
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, data.Attempt.Id));
            Assert.Equal(PayoutBatchStates.Reserved, await GetBatchStateAsync(con, data.Batch.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_ExactSenderRegisteredExecutesThroughExecutor()
    {
        var poolId = NewCommittedPoolId("exec_runner_exact_sender");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingProfile();
            var data = await CreatePreparedAttemptAsync(con, poolId, now, profile, ("addr-a", 1m));
            var sender = AcceptedSender("txid-runner-exact");
            var runner = NewRunner(new DictionaryProfileResolver(profile), RegistryFor(profile, sender));

            var result = await runner.ExecutePreparedAttemptsAsync(NewRequest(poolId, started: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.ExecutedAttemptCount);
            Assert.Equal(PayoutSendExecutionStatus.Accepted, result.ExecutionResults.Single().Status);
            Assert.Equal(1, sender.CallCount);
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, data.Attempt.Id));
            Assert.Equal("txid-runner-exact", await GetAttemptConfirmationAsync(con, data.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_AmbiguousSenderResultMarksAttemptAmbiguous()
    {
        var poolId = NewCommittedPoolId("exec_runner_ambiguous");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingProfile();
            var data = await CreatePreparedAttemptAsync(con, poolId, now, profile, ("addr-a", 1m));
            var sender = new StaticSender(PayoutAttemptSendResult.Ambiguous("wallet_timeout", "timeout"));
            var runner = NewRunner(new DictionaryProfileResolver(profile), RegistryFor(profile, sender));

            var result = await runner.ExecutePreparedAttemptsAsync(NewRequest(poolId, started: now.AddMinutes(1)), Ct);

            Assert.Equal(PayoutSendExecutionStatus.AmbiguousRequiresReview, result.ExecutionResults.Single().Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, data.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_FailedPreAcceptSenderResultMarksAttemptFailedPreAccept()
    {
        var poolId = NewCommittedPoolId("exec_runner_failed_pre_accept");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingProfile();
            var data = await CreatePreparedAttemptAsync(con, poolId, now, profile, ("addr-a", 1m));
            var sender = new StaticSender(PayoutAttemptSendResult.FailedPreAccept("validation", "not dispatched"));
            var runner = NewRunner(new DictionaryProfileResolver(profile), RegistryFor(profile, sender));

            var result = await runner.ExecutePreparedAttemptsAsync(NewRequest(poolId, started: now.AddMinutes(1)), Ct);

            Assert.Equal(PayoutSendExecutionStatus.FailedPreAccept, result.ExecutionResults.Single().Status);
            Assert.Equal(PayoutSendAttemptStates.FailedPreAccept, await GetAttemptStateAsync(con, data.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_SenderExceptionBecomesAmbiguousThroughExecutor()
    {
        var poolId = NewCommittedPoolId("exec_runner_sender_exception");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingProfile();
            var data = await CreatePreparedAttemptAsync(con, poolId, now, profile, ("addr-a", 1m));
            var sender = new ThrowingSender(new InvalidOperationException("raw-secret"));
            var runner = NewRunner(new DictionaryProfileResolver(profile), RegistryFor(profile, sender));

            var result = await runner.ExecutePreparedAttemptsAsync(NewRequest(poolId, started: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.ExecutedAttemptCount);
            Assert.Equal(0, result.FailureCount);
            Assert.Equal(PayoutSendExecutionStatus.SenderFailedAmbiguous, result.ExecutionResults.Single().Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, data.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_UnexpectedPerAttemptExceptionIsRecordedAndDoesNotCrashTick()
    {
        var poolId = NewCommittedPoolId("exec_runner_records_failure");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var failingProfile = MatchingProfile() with { CoinKey = "failing-coin" };
            var executableProfile = MatchingProfile() with { CoinKey = "failure-then-eligible-coin" };
            var failing = await CreatePreparedAttemptAsync(con, poolId, now, failingProfile, ("addr-a", 1m));
            var executable = await CreatePreparedAttemptAsync(con, poolId, now.AddSeconds(1), executableProfile,
                ("addr-b", 1m));
            var runner = NewRunner(new SelectiveThrowingProfileResolver("failing-coin", executableProfile),
                RegistryFor(executableProfile, AcceptedSender("txid-after-failure")));

            var result = await runner.ExecutePreparedAttemptsAsync(NewRequest(poolId, started: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.FailureCount);
            Assert.Equal(1, result.ExecutedAttemptCount);
            Assert.Equal(failing.Attempt.Id, result.Failures.Single().AttemptId);
            Assert.Equal(nameof(InvalidOperationException), result.Failures.Single().ErrorType);
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, failing.Attempt.Id));
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, executable.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_ContinuesAfterSkippedAttemptAndExecutesEligibleAttempt()
    {
        var poolId = NewCommittedPoolId("exec_runner_skip_then_execute");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var skippedProfile = MatchingProfile() with { CoinKey = "unsupported-coin" };
            var executableProfile = MatchingProfile() with { CoinKey = "eligible-coin" };
            var skipped = await CreatePreparedAttemptAsync(con, poolId, now, skippedProfile, ("addr-a", 1m));
            var executable = await CreatePreparedAttemptAsync(con, poolId, now.AddSeconds(1), executableProfile,
                ("addr-b", 1m));
            var sender = AcceptedSender("txid-after-skip");
            var runner = NewRunner(new DictionaryProfileResolver(executableProfile), RegistryFor(executableProfile, sender));

            var result = await runner.ExecutePreparedAttemptsAsync(NewRequest(poolId, maxAttempts: 10,
                started: now.AddMinutes(1)), Ct);

            Assert.Equal(2, result.CandidateAttemptCount);
            Assert.Equal(1, result.SkippedAttemptCount);
            Assert.Equal(1, result.ExecutedAttemptCount);
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, skipped.Attempt.Id));
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, executable.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptsAsync_CancellationIsPropagated()
    {
        var poolId = NewCommittedPoolId("exec_runner_cancelled");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingProfile();
            await CreatePreparedAttemptAsync(con, poolId, now, profile, ("addr-a", 1m));
            using var cts = new CancellationTokenSource();
            var runner = NewRunner(new DictionaryProfileResolver(profile),
                RegistryFor(profile, new CancellingSender(cts)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.ExecutePreparedAttemptsAsync(NewRequest(poolId, started: now.AddMinutes(1)), cts.Token));
        });
    }

    private DbPayoutExecutionRunner NewRunner(IPayoutProfileResolver resolver, IPayoutAttemptSenderRegistry registry)
    {
        var cf = new PgConnectionFactory(GetConnectionString());
        return new DbPayoutExecutionRunner(cf, payoutIntentRepo,
            new PayoutSendExecutorService(cf, payoutIntentRepo, resolver), resolver, registry);
    }

    private static PayoutExecutionRunnerRequest NewRequest(string poolId, int maxAttempts = 10,
        DateTime? started = null)
    {
        return new PayoutExecutionRunnerRequest
        {
            PoolId = poolId,
            MaxAttempts = maxAttempts,
            Started = started ?? UtcNow()
        };
    }

    private static PayoutAttemptSenderRegistry EmptyRegistry()
    {
        return new PayoutAttemptSenderRegistry(Array.Empty<PayoutAttemptSenderRegistration>());
    }

    private static PayoutAttemptSenderRegistry RegistryFor(PayoutProfile profile, IPayoutAttemptSender sender)
    {
        return new PayoutAttemptSenderRegistry(new[]
        {
            new PayoutAttemptSenderRegistration
            {
                Key = new PayoutAttemptSenderKey
                {
                    CoinFamily = profile.CoinFamily,
                    AdapterId = profile.AdapterId,
                    SendShape = profile.SendShape,
                    SendMethod = profile.SendMethod
                },
                Sender = sender
            }
        });
    }

    private static StaticSender AcceptedSender(string txId)
    {
        return new StaticSender(PayoutAttemptSendResult.Accepted(new PayoutAttemptEvidence
        {
            Kind = PayoutExternalConfirmationKinds.TxId,
            Value = txId
        }));
    }

    private static PayoutProfile MatchingProfile()
    {
        return new PayoutProfile
        {
            CoinKey = PoolCoin,
            CoinFamily = CoinFamily,
            AdapterId = Handler,
            SendShape = PayoutSendShapes.BatchMultiRecipient,
            SendMethod = Method,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
            ReservationReady = true
        };
    }

    private async Task<TestPayoutData> CreatePreparedAttemptAsync(NpgsqlConnection con, string poolId,
        DateTime created, PayoutProfile profile, params (string address, decimal amount)[] intents)
    {
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            var batch = await CreateBatchAsync(con, tx, poolId, created, profile, intents);
            var attempt = await CreateAttemptAsync(con, tx, batch, created, profile,
                batch.Intents.Select(x => x.Id).ToArray());
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
        DateTime created, PayoutProfile profile, params (string address, decimal amount)[] intents)
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
            Coin = profile.CoinKey,
            CoinFamily = profile.CoinFamily,
            Handler = profile.AdapterId,
            SendShape = profile.SendShape,
            RecipientSetHash = $"execution-runner-recipient-set-{Guid.NewGuid():N}",
            MinimumAmount = 0m,
            ReservedAmountSnapshot = intents.Sum(x => x.amount),
            IntentCountSnapshot = intents.Length,
            Created = created
        }, intentRequests, Ct);
    }

    private Task<PayoutSendAttempt> CreateAttemptAsync(NpgsqlConnection con, NpgsqlTransaction tx,
        PayoutBatch batch, DateTime created, PayoutProfile profile, params long[] intentIds)
    {
        var selectedIntents = batch.Intents.Where(x => intentIds.Contains(x.Id)).ToArray();

        return payoutIntentRepo.CreateSendAttemptAsync(con, tx, new CreatePayoutSendAttemptRequest
        {
            BatchId = batch.Id,
            PoolId = batch.PoolId,
            Coin = batch.Coin,
            AttemptNo = 1,
            Method = profile.SendMethod,
            RequestHash = $"request-hash-{Guid.NewGuid():N}",
            RequestSummary = $"test:recipients={selectedIntents.Length}",
            RecipientCount = selectedIntents.Length,
            AmountSnapshot = selectedIntents.Sum(x => x.Amount),
            Created = created
        }, intentIds, Ct);
    }

    private static Task<string> GetAttemptStateAsync(NpgsqlConnection con, long attemptId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_send_attempts WHERE id = @attemptid",
            new { attemptid = attemptId });
    }

    private static Task<string> GetBatchStateAsync(NpgsqlConnection con, long batchId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_batches WHERE id = @batchid",
            new { batchid = batchId });
    }

    private static Task<string> GetAttemptConfirmationAsync(NpgsqlConnection con, long attemptId)
    {
        return con.QuerySingleOrDefaultAsync<string>(@"SELECT transactionconfirmationdata
            FROM payout_send_attempts WHERE id = @attemptid",
            new { attemptid = attemptId });
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private record TestPayoutData(PayoutBatch Batch, PayoutSendAttempt Attempt);

    private class StaticSender : IPayoutAttemptSender
    {
        public StaticSender(PayoutAttemptSendResult result)
        {
            this.result = result;
        }

        private readonly PayoutAttemptSendResult result;
        public int CallCount { get; private set; }

        public Task<PayoutAttemptSendResult> SendAsync(PayoutSendExecutionContext context, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private class ThrowingSender : IPayoutAttemptSender
    {
        public ThrowingSender(Exception exception)
        {
            this.exception = exception;
        }

        private readonly Exception exception;

        public Task<PayoutAttemptSendResult> SendAsync(PayoutSendExecutionContext context, CancellationToken ct)
        {
            throw exception;
        }
    }

    private class CancellingSender : IPayoutAttemptSender
    {
        public CancellingSender(CancellationTokenSource cts)
        {
            this.cts = cts;
        }

        private readonly CancellationTokenSource cts;

        public Task<PayoutAttemptSendResult> SendAsync(PayoutSendExecutionContext context, CancellationToken ct)
        {
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }
    }

    private class SelectiveThrowingProfileResolver : IPayoutProfileResolver
    {
        public SelectiveThrowingProfileResolver(string throwingCoinKey, params PayoutProfile[] profiles)
        {
            this.throwingCoinKey = throwingCoinKey;
            this.profiles = profiles.ToDictionary(x => x.CoinKey, StringComparer.Ordinal);
        }

        private readonly string throwingCoinKey;
        private readonly Dictionary<string, PayoutProfile> profiles;

        public PayoutProfileResolution Resolve(string coinKey)
        {
            if(string.Equals(coinKey, throwingCoinKey, StringComparison.Ordinal))
                throw new InvalidOperationException("resolver failed");

            return profiles.TryGetValue(coinKey, out var profile)
                ? PayoutProfileResolution.Resolved(profile)
                : PayoutProfileResolution.Unsupported("coin not configured");
        }
    }

    private class DictionaryProfileResolver : IPayoutProfileResolver
    {
        public DictionaryProfileResolver(params PayoutProfile[] profiles)
        {
            this.profiles = profiles.ToDictionary(x => x.CoinKey, StringComparer.Ordinal);
        }

        private readonly Dictionary<string, PayoutProfile> profiles;

        public PayoutProfileResolution Resolve(string coinKey)
        {
            return profiles.TryGetValue(coinKey, out var profile)
                ? PayoutProfileResolution.Resolved(profile)
                : PayoutProfileResolution.Unsupported("coin not configured");
        }
    }
}
