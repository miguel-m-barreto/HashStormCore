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

public class PayoutOperationIdReconciliationServiceTests : PostgresCommittedIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository repo = new();

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_AttachesResolvedTxIdForAcceptedOperationIdAttempt()
    {
        var poolId = NewCommittedPoolId("opid_resolved_accepted");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-accepted");
            await InsertBalanceAsync(con, poolId, "addr-balance", 10m, now);
            var statesBefore = await GetStatesAsync(con, data);
            var balanceBefore = await SumBalancesAsync(con, poolId);

            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-resolved"));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewService().ReconcileOperationIdCandidateAsync(candidate, now.AddMinutes(1),
                provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.EvidenceAttachedCount);
            Assert.Equal(new[] { data.Attempt.Id }, result.EvidenceAttachedAttemptIds.ToArray());
            Assert.Equal(1, provider.CallCount);
            Assert.Equal("opid-accepted", provider.OperationIds.Single());
            Assert.Equal(1, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId, "txid-resolved"));
            Assert.Equal(statesBefore, await GetStatesAsync(con, data));
            Assert.Equal(balanceBefore, await SumBalancesAsync(con, poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "balance_changes", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_AttachesResolvedTxIdForAmbiguousOperationIdAttemptWithoutStateTransition()
    {
        var poolId = NewCommittedPoolId("opid_resolved_ambiguous");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAmbiguousOperationIdAttemptAsync(con, poolId, now, "opid-ambiguous");
            var statesBefore = await GetStatesAsync(con, data);
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-ambiguous"));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewService().ReconcileOperationIdCandidateAsync(candidate, now.AddMinutes(1),
                provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.EvidenceAttachedCount);
            Assert.Equal(1, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId, "txid-ambiguous"));
            Assert.Equal(statesBefore, await GetStatesAsync(con, data));
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, data.Batch.Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, data.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_PendingDoesNotMutateDatabase()
    {
        var poolId = NewCommittedPoolId("opid_pending");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-pending");
            var confirmationsBefore = await CountConfirmationsAsync(con, poolId);
            var statesBefore = await GetStatesAsync(con, data);
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.Pending("not_done"));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewService().ReconcileOperationIdCandidateAsync(candidate, now.AddMinutes(1),
                provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.ProviderPendingCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(confirmationsBefore, await CountConfirmationsAsync(con, poolId));
            Assert.Equal(statesBefore, await GetStatesAsync(con, data));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_ProvenNoAcceptNeedsReviewWithoutStateChange()
    {
        var poolId = NewCommittedPoolId("opid_proven_no_accept");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-no-accept");
            var statesBefore = await GetStatesAsync(con, data);
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ProvenNoAccept("not_broadcast"));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewService().ReconcileOperationIdCandidateAsync(candidate, now.AddMinutes(1),
                provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.NeedsReviewCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(statesBefore, await GetStatesAsync(con, data));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_UnknownNeedsReviewWithoutStateChange()
    {
        var poolId = NewCommittedPoolId("opid_unknown");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-unknown");
            var statesBefore = await GetStatesAsync(con, data);
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.Unknown("unclear"));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewService().ReconcileOperationIdCandidateAsync(candidate, now.AddMinutes(1),
                provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.NeedsReviewCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(statesBefore, await GetStatesAsync(con, data));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_DoesNotDuplicateExistingTxIdEvidence()
    {
        var poolId = NewCommittedPoolId("opid_existing_txid");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-existing");
            await InsertTxIdConfirmationAsync(con, data, "txid-existing", now.AddMinutes(1));
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-existing"));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewService().ReconcileOperationIdCandidateAsync(candidate, now.AddMinutes(2),
                provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.AlreadyAttachedCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(1, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId, "txid-existing"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_ProviderExceptionDoesNotMutateOrStoreRawMessage()
    {
        var poolId = NewCommittedPoolId("opid_provider_exception");
        const string rawPayload = "RAW_PROVIDER_PAYLOAD_SECRET";

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-exception");
            var confirmationsBefore = await CountConfirmationsAsync(con, poolId);
            var statesBefore = await GetStatesAsync(con, data);
            var provider = new ThrowingOperationStatusProvider(new InvalidOperationException(rawPayload));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewService().ReconcileOperationIdCandidateAsync(candidate, now.AddMinutes(1),
                provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.ProviderErrorCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(confirmationsBefore, await CountConfirmationsAsync(con, poolId));
            Assert.Equal(statesBefore, await GetStatesAsync(con, data));
            Assert.Equal(0, await CountConfirmationsContainingValueAsync(con, poolId, rawPayload));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_ProviderCanReadCommittedRowsOutsideServiceTransaction()
    {
        var poolId = NewCommittedPoolId("opid_provider_outside_tx");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-outside-tx");
            var provider = new InspectingOperationStatusProvider(GetConnectionString(), "txid-outside-tx");
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewService().ReconcileOperationIdCandidateAsync(candidate, now.AddMinutes(1),
                provider, Ct);

            Assert.Equal(1, result.EvidenceAttachedCount);
            Assert.True(provider.ObservedCommittedAttempt);
            Assert.Contains(data.Attempt.Id, provider.AttemptIds);
            Assert.Equal(1, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId, "txid-outside-tx"));
        });
    }

    [Fact]
    public void ReconcileOperationIdsAsync_PublicBulkProviderMethodIsNotExposed()
    {
        var methods = typeof(PayoutOperationIdReconciliationService)
            .GetMethods()
            .Where(x => x.Name == "ReconcileOperationIdsAsync");

        Assert.Empty(methods);
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_RejectsInvalidCandidateAndProvider()
    {
        var poolId = NewCommittedPoolId("opid_invalid_candidate");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-invalid");
            var service = NewService();
            var candidate = await LoadSingleCandidateAsync(con, poolId);
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.Pending());

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ReconcileOperationIdCandidateAsync(null, now.AddMinutes(1), provider, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ReconcileOperationIdCandidateAsync(candidate, now.AddMinutes(1), null, Ct));
        });
    }

    // ── Profile-aware reconciliation tests ───────────────────────────────────

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_ConstructorRequiresProfileResolver()
    {
        var poolId = NewCommittedPoolId("opid_constructor_requires_resolver");

        return WithCommittedCleanupAsync(poolId, _ =>
        {
            Assert.Throws<ArgumentNullException>(() =>
                new PayoutOperationIdReconciliationService(new PgConnectionFactory(GetConnectionString()), repo, null));
            return Task.CompletedTask;
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_ProfileNotReadySkipsTxIdAttachmentCountsAsNeedsReview()
    {
        var poolId = NewCommittedPoolId("opid_profile_not_ready");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-not-ready");
            var confirmationsBefore = await CountConfirmationsAsync(con, poolId);
            var statesBefore = await GetStatesAsync(con, data);
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-should-not-attach"));
            var resolver = new FixedProfileResolver(PayoutProfileResolution.Unsupported("coin not configured"));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewServiceWithResolver(resolver).ReconcileOperationIdCandidateAsync(
                candidate, now.AddMinutes(1), provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.NeedsReviewCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, result.ProviderErrorCount);
            Assert.Equal(confirmationsBefore, await CountConfirmationsAsync(con, poolId));
            Assert.Equal(statesBefore, await GetStatesAsync(con, data));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_NonAsyncProfileSkipsTxIdAttachmentCountsAsNeedsReview()
    {
        var poolId = NewCommittedPoolId("opid_non_async_profile");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-direct");
            var confirmationsBefore = await CountConfirmationsAsync(con, poolId);
            var statesBefore = await GetStatesAsync(con, data);
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-direct-bypass"));
            // TxId-only direct profile is not eligible for reconciliation
            var txIdProfile = new PayoutProfile
            {
                CoinKey = "test-txid",
                CoinFamily = "bitcoin",
                AdapterId = "bitcoin-rpc",
                SendShape = PayoutSendShapes.BatchMultiRecipient,
                SendMethod = "sendmany",
                SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
                ReservationReady = true
            };
            var resolver = new FixedProfileResolver(PayoutProfileResolution.Resolved(txIdProfile));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewServiceWithResolver(resolver).ReconcileOperationIdCandidateAsync(
                candidate, now.AddMinutes(1), provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.NeedsReviewCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, result.ProviderErrorCount);
            Assert.Equal(confirmationsBefore, await CountConfirmationsAsync(con, poolId));
            Assert.Equal(statesBefore, await GetStatesAsync(con, data));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_UnsafeFakeTxIdFromProviderIsRejectedAsNeedsReview()
    {
        var poolId = NewCommittedPoolId("opid_unsafe_txid");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-unsafe");
            var confirmationsBefore = await CountConfirmationsAsync(con, poolId);
            var statesBefore = await GetStatesAsync(con, data);
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("send:fake-txid-inject"));
            var asyncProfile = MatchingAsyncOperationProfile();
            var resolver = new FixedProfileResolver(PayoutProfileResolution.Resolved(asyncProfile));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewServiceWithResolver(resolver).ReconcileOperationIdCandidateAsync(
                candidate, now.AddMinutes(1), provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.NeedsReviewCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, result.ProviderErrorCount);
            Assert.Equal(confirmationsBefore, await CountConfirmationsAsync(con, poolId));
            Assert.Equal(statesBefore, await GetStatesAsync(con, data));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileOperationIdCandidateAsync_MetadataMismatchSkipsTxIdAttachmentCountsAsNeedsReview()
    {
        var poolId = NewCommittedPoolId("opid_metadata_mismatch");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedOperationIdAttemptAsync(con, poolId, now, "opid-metadata-mismatch");
            var confirmationsBefore = await CountConfirmationsAsync(con, poolId);
            var statesBefore = await GetStatesAsync(con, data);
            var provider = new StaticOperationStatusProvider(PayoutOperationStatusResult.ResolvedTxId("txid-should-not-attach"));
            var mismatchProfile = MatchingAsyncOperationProfile() with { AdapterId = "different-handler" };
            var resolver = new FixedProfileResolver(PayoutProfileResolution.Resolved(mismatchProfile));
            var candidate = await LoadSingleCandidateAsync(con, poolId);

            var result = await NewServiceWithResolver(resolver).ReconcileOperationIdCandidateAsync(
                candidate, now.AddMinutes(1), provider, Ct);

            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.NeedsReviewCount);
            Assert.Equal(0, result.EvidenceAttachedCount);
            Assert.Equal(0, provider.CallCount);
            Assert.Equal(confirmationsBefore, await CountConfirmationsAsync(con, poolId));
            Assert.Equal(statesBefore, await GetStatesAsync(con, data));
        });
    }

    private PayoutOperationIdReconciliationService NewService()
    {
        return NewServiceWithResolver(
            new FixedProfileResolver(PayoutProfileResolution.Resolved(MatchingAsyncOperationProfile())));
    }

    private PayoutOperationIdReconciliationService NewServiceWithResolver(IPayoutProfileResolver resolver)
    {
        return new PayoutOperationIdReconciliationService(new PgConnectionFactory(GetConnectionString()), repo, resolver);
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

    private async Task<PayoutReconciliationAttemptSummary> LoadSingleCandidateAsync(NpgsqlConnection con, string poolId)
    {
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            var candidates = await repo.GetAttemptsWithOperationIdAsync(con, tx, poolId, 10, Ct);
            await tx.CommitAsync();
            return Assert.Single(candidates);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private async Task<TestPayoutData> CreateAcceptedOperationIdAttemptAsync(NpgsqlConnection con, string poolId,
        DateTime created, string operationId, string address = "addr-a", decimal amount = 1m)
    {
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            var batch = await CreateBatchAsync(con, tx, poolId, created, (address, amount));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, $"request-hash-{Guid.NewGuid():N}", created,
                batch.Intents[0].Id);

            await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, created.AddMinutes(1), Ct);
            await repo.MarkAttemptAcceptedAsync(con, tx, attempt.Id, poolId,
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

    private async Task<TestPayoutData> CreateAmbiguousOperationIdAttemptAsync(NpgsqlConnection con, string poolId,
        DateTime created, string operationId)
    {
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            var batch = await CreateBatchAsync(con, tx, poolId, created, ("addr-a", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, $"request-hash-{Guid.NewGuid():N}", created,
                batch.Intents[0].Id);

            await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, created.AddMinutes(1), Ct);
            await repo.InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
            {
                PoolId = poolId,
                Coin = Coin,
                BatchId = batch.Id,
                AttemptId = attempt.Id,
                Kind = PayoutExternalConfirmationKinds.OperationId,
                Value = operationId,
                Created = created.AddMinutes(2)
            }, Ct);
            await repo.MarkAttemptAmbiguousAsync(con, tx, attempt.Id, poolId, "review_required",
                "operation status unknown", created.AddMinutes(3), Ct);

            await tx.CommitAsync();
            return new TestPayoutData(batch, attempt);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private Task<PayoutBatch> CreateBatchAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId, DateTime created,
        params (string address, decimal amount)[] intents)
    {
        var intentRequests = intents.Select(x => new CreatePayoutIntentRequest
        {
            Address = x.address,
            Amount = x.amount,
            BalanceSnapshotAmount = x.amount,
            BalanceSnapshotUpdated = created,
            PaymentThreshold = 0m
        }).ToArray();

        return repo.CreateReservedBatchAsync(con, tx, new CreatePayoutBatchRequest
        {
            PoolId = poolId,
            Coin = Coin,
            CoinFamily = CoinFamily,
            Handler = Handler,
            SendShape = PayoutSendShapes.AsyncOperation,
            RecipientSetHash = $"recipient-set-{Guid.NewGuid():N}",
            MinimumAmount = 0m,
            ReservedAmountSnapshot = intents.Sum(x => x.amount),
            IntentCountSnapshot = intents.Length,
            Created = created
        }, intentRequests, Ct);
    }

    private Task<PayoutSendAttempt> CreateAttemptAsync(NpgsqlConnection con, NpgsqlTransaction tx, PayoutBatch batch,
        int attemptNo, string requestHash, DateTime created, params long[] intentIds)
    {
        var selectedIntents = batch.Intents.Where(x => intentIds.Contains(x.Id)).ToArray();

        return repo.CreateSendAttemptAsync(con, tx, new CreatePayoutSendAttemptRequest
        {
            BatchId = batch.Id,
            PoolId = batch.PoolId,
            Coin = batch.Coin,
            AttemptNo = attemptNo,
            Method = Method,
            RequestHash = requestHash,
            RequestSummary = $"test:recipients={selectedIntents.Length}",
            RecipientCount = selectedIntents.Length,
            AmountSnapshot = selectedIntents.Sum(x => x.Amount),
            Created = created
        }, intentIds, Ct);
    }

    private async Task InsertTxIdConfirmationAsync(NpgsqlConnection con, TestPayoutData data, string txId, DateTime created)
    {
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            await repo.InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
            {
                PoolId = data.Batch.PoolId,
                Coin = data.Batch.Coin,
                BatchId = data.Batch.Id,
                AttemptId = data.Attempt.Id,
                Kind = PayoutExternalConfirmationKinds.TxId,
                Value = txId,
                Created = created
            }, Ct);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static PayoutAttemptEvidence NewEvidence(string kind, string value)
    {
        return new PayoutAttemptEvidence
        {
            Kind = kind,
            Value = value
        };
    }

    private static Task InsertBalanceAsync(NpgsqlConnection con, string poolId, string address, decimal amount, DateTime created)
    {
        return con.ExecuteAsync(@"INSERT INTO balances(poolid, address, amount, created, updated)
            VALUES(@poolid, @address, @amount, @created, @created)",
            new { poolid = poolId, address, amount, created });
    }

    private static Task<string> GetBatchStateAsync(NpgsqlConnection con, long batchId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_batches WHERE id = @batchid",
            new { batchid = batchId });
    }

    private static Task<string> GetAttemptStateAsync(NpgsqlConnection con, long attemptId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_send_attempts WHERE id = @attemptid",
            new { attemptid = attemptId });
    }

    private static Task<string> GetIntentStateAsync(NpgsqlConnection con, long intentId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_intents WHERE id = @intentid",
            new { intentid = intentId });
    }

    private static async Task<PayoutStateSnapshot> GetStatesAsync(NpgsqlConnection con, TestPayoutData data)
    {
        return new PayoutStateSnapshot(
            await GetBatchStateAsync(con, data.Batch.Id),
            await GetAttemptStateAsync(con, data.Attempt.Id),
            await GetIntentStateAsync(con, data.Batch.Intents[0].Id));
    }

    private static Task<int> CountConfirmationsAsync(NpgsqlConnection con, string poolId)
    {
        return con.QuerySingleAsync<int>("SELECT COUNT(*) FROM payout_external_confirmations WHERE poolid = @poolid",
            new { poolid = poolId });
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

    private static Task<int> CountConfirmationsContainingValueAsync(NpgsqlConnection con, string poolId, string value)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_external_confirmations
            WHERE poolid = @poolid AND value LIKE @value",
            new { poolid = poolId, value = $"%{value}%" });
    }

    private static Task<decimal> SumBalancesAsync(NpgsqlConnection con, string poolId)
    {
        return con.QuerySingleAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM balances WHERE poolid = @poolid",
            new { poolid = poolId });
    }

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid", new { poolid = poolId });
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private record TestPayoutData(PayoutBatch Batch, PayoutSendAttempt Attempt);
    private record PayoutStateSnapshot(string BatchState, string AttemptState, string IntentState);

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

    private class InspectingOperationStatusProvider : IPayoutOperationStatusProvider
    {
        public InspectingOperationStatusProvider(string connectionString, string txId)
        {
            this.connectionString = connectionString;
            this.txId = txId;
        }

        private readonly string connectionString;
        private readonly string txId;
        public bool ObservedCommittedAttempt { get; private set; }
        public List<long> AttemptIds { get; } = new();

        public async Task<PayoutOperationStatusResult> GetOperationStatusAsync(PayoutReconciliationAttemptSummary attempt,
            string operationId, CancellationToken ct)
        {
            AttemptIds.Add(attempt.AttemptId);

            await using var con = new NpgsqlConnection(connectionString);
            await con.OpenAsync(ct);

            var attemptCount = await con.QuerySingleAsync<int>(@"SELECT COUNT(*)
                FROM payout_send_attempts psa
                JOIN payout_external_confirmations pec ON pec.batchid = psa.batchid
                    AND pec.attemptid = psa.id
                    AND pec.poolid = psa.poolid
                    AND pec.coin = psa.coin
                WHERE psa.id = @attemptid
                  AND pec.kind = @kind
                  AND pec.value = @operationid",
                new
                {
                    attemptid = attempt.AttemptId,
                    kind = PayoutExternalConfirmationKinds.OperationId,
                    operationid = operationId
                });

            ObservedCommittedAttempt = attemptCount == 1;
            return PayoutOperationStatusResult.ResolvedTxId(txId);
        }
    }

    private class FixedProfileResolver : IPayoutProfileResolver
    {
        public FixedProfileResolver(PayoutProfileResolution resolution)
        {
            this.resolution = resolution;
        }

        private readonly PayoutProfileResolution resolution;

        public PayoutProfileResolution Resolve(string coinKey) => resolution;
    }
}
