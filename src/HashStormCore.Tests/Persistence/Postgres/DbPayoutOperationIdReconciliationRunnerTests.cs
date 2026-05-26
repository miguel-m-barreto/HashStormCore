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

public class DbPayoutOperationIdReconciliationRunnerTests : PostgresCommittedIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository payoutIntentRepo = new();

    [PostgresIntegrationFact]
    public async Task ReconcileOperationIdsAsync_ValidatesRequest()
    {
        var runner = NewRunner(new DictionaryProfileResolver(), EmptyRegistry());
        var now = UtcNow();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ReconcileOperationIdsAsync(NewRequest(" ", checkedAt: now), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.ReconcileOperationIdsAsync(NewRequest("pool", limit: 0, checkedAt: now), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ReconcileOperationIdsAsync(NewRequest("pool", checkedAt: default), Ct));
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_PropagatesCancellation()
    {
        var runner = NewRunner(new DictionaryProfileResolver(), EmptyRegistry());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        return Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.ReconcileOperationIdsAsync(NewRequest("pool"), cts.Token));
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_UnsupportedProfileSkipsWithoutProviderCall()
    {
        var poolId = NewCommittedPoolId("opid_runner_unsupported");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, MatchingAsyncOperationProfile(),
                "opid-unsupported");
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-no-call"));
            var runner = NewRunner(new DictionaryProfileResolver(), RegistryFor(MatchingAsyncOperationProfile(), provider));

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, provider.CallCount);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_NotReadyProfileSkipsWithoutProviderCall()
    {
        var poolId = NewCommittedPoolId("opid_runner_not_ready");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingAsyncOperationProfile();
            await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, profile, "opid-not-ready");
            var notReadyProfile = profile with { ReservationReady = false, NotReadyReason = "not ready" };
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-no-call"));
            var runner = NewRunner(new DictionaryProfileResolver(notReadyProfile), RegistryFor(profile, provider));

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, provider.CallCount);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_NonOperationIdProfileSkipsWithoutProviderCall()
    {
        var poolId = NewCommittedPoolId("opid_runner_non_async");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var candidateProfile = MatchingAsyncOperationProfile();
            await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, candidateProfile, "opid-non-async");
            var txIdProfile = candidateProfile with
            {
                SendShape = PayoutSendShapes.BatchMultiRecipient,
                SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
                RequiresOperationIdProvider = false,
                SupportsShieldedOperationTracking = false
            };
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-no-call"));
            var runner = NewRunner(new DictionaryProfileResolver(txIdProfile), RegistryFor(txIdProfile, provider));

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, provider.CallCount);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_MetadataMismatchSkipsWithoutProviderCall()
    {
        var poolId = NewCommittedPoolId("opid_runner_metadata_mismatch");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingAsyncOperationProfile();
            await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, profile, "opid-mismatch");
            var mismatchProfile = profile with { AdapterId = "different-handler" };
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-no-call"));
            var runner = NewRunner(new DictionaryProfileResolver(mismatchProfile), RegistryFor(mismatchProfile, provider));

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, provider.CallCount);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_NoProviderRegisteredSkipsWithoutTxIdAttachment()
    {
        var poolId = NewCommittedPoolId("opid_runner_no_provider");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingAsyncOperationProfile();
            await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, profile, "opid-no-provider");
            var runner = NewRunner(new DictionaryProfileResolver(profile), EmptyRegistry());

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Contains("no registered operation status provider", Assert.Single(result.SkippedAttempts).Reason);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_ExactProviderRegisteredAttachesTxIdThroughServicePath()
    {
        var poolId = NewCommittedPoolId("opid_runner_attaches");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingAsyncOperationProfile();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, profile, "opid-attaches");
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-runner"));
            var runner = NewRunner(new DictionaryProfileResolver(profile), RegistryFor(profile, provider));

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.EvidenceAttachedCount);
            Assert.Equal(new[] { data.Attempt.Id }, result.EvidenceAttachedAttemptIds.ToArray());
            Assert.Equal(1, provider.CallCount);
            Assert.Equal("opid-attaches", provider.OperationIds.Single());
            Assert.Equal(1, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId,
                "txid-runner"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_ProviderPendingCountsWithoutTxIdAttachment()
    {
        var poolId = NewCommittedPoolId("opid_runner_pending");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingAsyncOperationProfile();
            await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, profile, "opid-pending");
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.Pending("not_done"));
            var runner = NewRunner(new DictionaryProfileResolver(profile), RegistryFor(profile, provider));

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.ProviderPendingCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(1, provider.CallCount);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_UnsafeProviderTxIdCountsNeedsReviewWithoutAttachment()
    {
        var poolId = NewCommittedPoolId("opid_runner_unsafe_txid");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingAsyncOperationProfile();
            await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, profile, "opid-unsafe");
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("send:fake-txid"));
            var runner = NewRunner(new DictionaryProfileResolver(profile), RegistryFor(profile, provider));

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.NeedsReviewCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(1, provider.CallCount);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_PlaceholderProviderTxIdCountsNeedsReviewWithoutAttachment()
    {
        var poolId = NewCommittedPoolId("opid_runner_placeholder_txid");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingAsyncOperationProfile();
            await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, profile, "opid-placeholder");
            var provider = new StaticOperationStatusProvider(
                PayoutOperationStatusResult.ResolvedTxId("fake-placeholder-txid"));
            var runner = NewRunner(new DictionaryProfileResolver(profile), RegistryFor(profile, provider));

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.NeedsReviewCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(1, provider.CallCount);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_ProviderExceptionCountsProviderErrorWithoutCrashingTick()
    {
        var poolId = NewCommittedPoolId("opid_runner_provider_exception");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = MatchingAsyncOperationProfile();
            await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, profile, "opid-exception");
            var provider = new ThrowingOperationStatusProvider(new InvalidOperationException("raw-secret"));
            var runner = NewRunner(new DictionaryProfileResolver(profile), RegistryFor(profile, provider));

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(1, result.ProviderErrorCount);
            Assert.Equal(0, result.FailureCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdsAsync_ContinuesAfterSkippedCandidateAndProcessesEligibleCandidate()
    {
        var poolId = NewCommittedPoolId("opid_runner_skip_then_attach");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var skippedProfile = MatchingAsyncOperationProfile() with { CoinKey = "unsupported-coin" };
            var eligibleProfile = MatchingAsyncOperationProfile() with { CoinKey = "eligible-coin" };
            var skipped = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, skippedProfile, "opid-skipped");
            var eligible = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now.AddSeconds(1),
                eligibleProfile, "opid-eligible");
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-after-skip"));
            var runner = NewRunner(new DictionaryProfileResolver(eligibleProfile), RegistryFor(eligibleProfile, provider));

            var result = await runner.ReconcileOperationIdsAsync(NewRequest(poolId, limit: 10,
                checkedAt: now.AddMinutes(1)), Ct);

            Assert.Equal(2, result.CandidateCount);
            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(1, result.EvidenceAttachedCount);
            Assert.Equal(skipped.Attempt.Id, Assert.Single(result.SkippedAttempts).AttemptId);
            Assert.Equal(new[] { eligible.Attempt.Id }, result.EvidenceAttachedAttemptIds.ToArray());
            Assert.Equal(1, provider.CallCount);
            Assert.Equal(0, await CountConfirmationsForAttemptAsync(con, skipped.Attempt.Id,
                PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(1, await CountConfirmationsForAttemptAsync(con, eligible.Attempt.Id,
                PayoutExternalConfirmationKinds.TxId));
        });
    }

    private DbPayoutOperationIdReconciliationRunner NewRunner(IPayoutProfileResolver resolver,
        IPayoutOperationStatusProviderRegistry registry)
    {
        var cf = new PgConnectionFactory(GetConnectionString());
        return new DbPayoutOperationIdReconciliationRunner(cf, payoutIntentRepo, resolver, registry,
            new PayoutOperationIdReconciliationService(cf, payoutIntentRepo, resolver));
    }

    private static PayoutOperationIdReconciliationRunnerRequest NewRequest(string poolId, int limit = 10,
        DateTime? checkedAt = null)
    {
        return new PayoutOperationIdReconciliationRunnerRequest
        {
            PoolId = poolId,
            Limit = limit,
            CheckedAt = checkedAt ?? UtcNow()
        };
    }

    private static PayoutOperationStatusProviderRegistry EmptyRegistry()
    {
        return new PayoutOperationStatusProviderRegistry(Array.Empty<PayoutOperationStatusProviderRegistration>());
    }

    private static PayoutOperationStatusProviderRegistry RegistryFor(PayoutProfile profile,
        IPayoutOperationStatusProvider provider)
    {
        return new PayoutOperationStatusProviderRegistry(new[]
        {
            new PayoutOperationStatusProviderRegistration
            {
                Key = new PayoutOperationStatusProviderKey
                {
                    CoinFamily = profile.CoinFamily,
                    AdapterId = profile.AdapterId,
                    SendShape = profile.SendShape,
                    SendMethod = profile.SendMethod
                },
                Provider = provider
            }
        });
    }

    private static PayoutProfile MatchingAsyncOperationProfile()
    {
        return new PayoutProfile
        {
            CoinKey = Coin,
            CoinFamily = CoinFamily,
            AdapterId = Handler,
            SendShape = PayoutSendShapes.AsyncOperation,
            SendMethod = Method,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId,
            RequiresOperationIdProvider = true,
            SupportsShieldedOperationTracking = true,
            ReservationReady = true
        };
    }

    private async Task<TestPayoutData> CreateAcceptedOperationIdAttemptAsync(NpgsqlConnection con, string poolId,
        DateTime created, PayoutProfile profile, string operationId, string address = "addr-a", decimal amount = 1m)
    {
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            var batch = await CreateBatchAsync(con, tx, poolId, created, profile, (address, amount));
            var attempt = await CreateAttemptAsync(con, tx, batch, created, profile, batch.Intents[0].Id);

            await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, created.AddMinutes(1), Ct);
            await payoutIntentRepo.MarkAttemptAcceptedAsync(con, tx, attempt.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.OperationId, operationId), created.AddMinutes(2), Ct);

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
            RecipientSetHash = $"opid-runner-recipient-set-{Guid.NewGuid():N}",
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

    private static PayoutAttemptEvidence NewEvidence(string kind, string value)
    {
        return new PayoutAttemptEvidence
        {
            Kind = kind,
            Value = value
        };
    }

    private static Task<int> CountConfirmationsAsync(NpgsqlConnection con, string poolId, string kind)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_external_confirmations
            WHERE poolid = @poolid AND kind = @kind",
            new { poolid = poolId, kind });
    }

    private static Task<int> CountConfirmationsAsync(NpgsqlConnection con, string poolId, string kind, string value)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_external_confirmations
            WHERE poolid = @poolid AND kind = @kind AND value = @value",
            new { poolid = poolId, kind, value });
    }

    private static Task<int> CountConfirmationsForAttemptAsync(NpgsqlConnection con, long attemptId, string kind)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_external_confirmations
            WHERE attemptid = @attemptid AND kind = @kind",
            new { attemptid = attemptId, kind });
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private record TestPayoutData(PayoutBatch Batch, PayoutSendAttempt Attempt);

    private class StaticOperationStatusProvider : IPayoutOperationStatusProvider
    {
        public StaticOperationStatusProvider(PayoutOperationStatusResult result)
        {
            this.result = result;
        }

        private readonly PayoutOperationStatusResult result;
        public int CallCount { get; private set; }
        public List<string> OperationIds { get; } = new();

        public Task<PayoutOperationStatusResult> GetOperationStatusAsync(PayoutReconciliationAttemptSummary attempt,
            string operationId, CancellationToken ct)
        {
            CallCount++;
            OperationIds.Add(operationId);
            return Task.FromResult(result);
        }
    }

    private class ThrowingOperationStatusProvider : IPayoutOperationStatusProvider
    {
        public ThrowingOperationStatusProvider(Exception exception)
        {
            this.exception = exception;
        }

        private readonly Exception exception;

        public Task<PayoutOperationStatusResult> GetOperationStatusAsync(PayoutReconciliationAttemptSummary attempt,
            string operationId, CancellationToken ct)
        {
            throw exception;
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
