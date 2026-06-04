using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace HashStormCore.Tests.Persistence.Postgres;

public class PayoutAmbiguousReviewServiceTests : PostgresIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository repo = new();
    private readonly PayoutAmbiguousReviewService service;

    public PayoutAmbiguousReviewServiceTests()
    {
        service = NewService(TxIdProfile());
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_AcceptsWithEvidenceAndSubmitsFinalBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_accept");
            var now = UtcNow();
            var data = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);
            await InsertBalanceAsync(con, tx, poolId, "addr-a", 10m, now);
            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);
            var balanceAmountBefore = await SumBalancesAsync(con, tx, poolId);

            var result = await service.ReviewAmbiguousAttemptAsync(con, tx, NewAcceptedRequest(data, now.AddMinutes(1),
                PayoutExternalConfirmationKinds.TxId, "txid-reviewed"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.Accepted, result.Status);
            Assert.Equal(data.Batch.Id, result.BatchId);
            Assert.Equal(data.Attempt.Id, result.AttemptId);
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, tx, data.Attempt.Id));
            Assert.Equal(PayoutAttemptIntentStates.Accepted,
                await GetAttemptIntentStateAsync(con, tx, data.Attempt.Id, data.Batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal(PayoutBatchStates.Submitted, await GetBatchStateAsync(con, tx, data.Batch.Id));
            Assert.Equal(1, await CountConfirmationsAsync(con, tx, data.Batch.Id, data.Attempt.Id,
                PayoutExternalConfirmationKinds.TxId, "txid-reviewed"));
            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(balanceAmountBefore, await SumBalancesAsync(con, tx, poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_AcceptsTargetAndLeavesSiblingAmbiguous()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_sibling");
            var now = UtcNow();
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m), ("addr-b", 2m));
            var target = await CreateAttemptAsync(con, tx, batch, 1, "review-sibling-target", now, batch.Intents[0].Id);
            var sibling = await CreateAttemptAsync(con, tx, batch, 2, "review-sibling-other", now, batch.Intents[1].Id);

            await repo.MarkAttemptSendingAsync(con, tx, target.Id, poolId, now, Ct);
            await repo.MarkStaleBatchAmbiguousAsync(con, tx, batch.Id, target.Id, poolId, "stale", null, now.AddMinutes(1), Ct);

            var result = await service.ReviewAmbiguousAttemptAsync(con, tx, NewAcceptedRequest(new TestPayoutData(batch, target),
                now.AddMinutes(2), PayoutExternalConfirmationKinds.TxId, "txid-reviewed-sibling"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.Accepted, result.Status);
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, tx, target.Id));
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, sibling.Id));
            Assert.Equal(PayoutAttemptIntentStates.AmbiguousRequiresReview,
                await GetAttemptIntentStateAsync(con, tx, sibling.Id, batch.Intents[1].Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, batch.Intents[1].Id));
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, tx, batch.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_TxIdProfileRejectsWrongOrUnsafeAcceptedEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_txid_reject");
            var now = UtcNow();
            var invalidEvidence = new[]
            {
                (PayoutExternalConfirmationKinds.RawHash, "rawhash-reviewed"),
                (PayoutExternalConfirmationKinds.OperationId, "operationid-reviewed"),
                (PayoutExternalConfirmationKinds.WalletAck, "wallet-ack-reviewed"),
                (PayoutExternalConfirmationKinds.TxId, "send:fake-reviewed"),
                (PayoutExternalConfirmationKinds.TxId, "placeholder-reviewed")
            };

            for(var i = 0; i < invalidEvidence.Length; i++)
            {
                var (kind, value) = invalidEvidence[i];
                var data = await CreateAmbiguousAttemptAsync(con, tx, poolId, now.AddMinutes(i));

                var result = await service.ReviewAmbiguousAttemptAsync(con, tx,
                    NewAcceptedRequest(data, now.AddMinutes(20 + i), kind, value), Ct);

                Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, result.Status);
                Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview,
                    await GetBatchStateAsync(con, tx, data.Batch.Id));
                Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview,
                    await GetAttemptStateAsync(con, tx, data.Attempt.Id));
                Assert.Equal(0, await CountConfirmationsAsync(con, tx, data.Batch.Id, data.Attempt.Id, kind, value));
            }
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_RawHashProfileAcceptsRawHashAndRejectsTxId()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_rawhash");
            var now = UtcNow();
            var acceptedData = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);
            var rawHashService = NewService(RawHashProfile());

            var accepted = await rawHashService.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(acceptedData, now.AddMinutes(1), PayoutExternalConfirmationKinds.RawHash,
                    "rawhash-reviewed"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.Accepted, accepted.Status);
            Assert.Equal(1, await CountConfirmationsAsync(con, tx, acceptedData.Batch.Id, acceptedData.Attempt.Id,
                PayoutExternalConfirmationKinds.RawHash, "rawhash-reviewed"));

            var rejectedData = await CreateAmbiguousAttemptAsync(con, tx, poolId, now.AddMinutes(2));
            var rejected = await rawHashService.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(rejectedData, now.AddMinutes(3), PayoutExternalConfirmationKinds.TxId,
                    "txid-wrong-for-rawhash"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, rejected.Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview,
                await GetAttemptStateAsync(con, tx, rejectedData.Attempt.Id));
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, rejectedData.Batch.Id, rejectedData.Attempt.Id,
                PayoutExternalConfirmationKinds.TxId, "txid-wrong-for-rawhash"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_AsyncOperationProfileAcceptsFinalTxIdButRejectsOperationId()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_async_final_txid");
            var now = UtcNow();
            var acceptedData = await CreateAmbiguousAttemptAsync(con, tx, poolId, now,
                sendShape: PayoutSendShapes.AsyncOperation);
            var asyncService = NewService(AsyncOperationProfile());

            var accepted = await asyncService.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(acceptedData, now.AddMinutes(1), PayoutExternalConfirmationKinds.TxId,
                    "txid-reviewed-async"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.Accepted, accepted.Status);
            Assert.Equal(1, await CountConfirmationsAsync(con, tx, acceptedData.Batch.Id, acceptedData.Attempt.Id,
                PayoutExternalConfirmationKinds.TxId, "txid-reviewed-async"));

            var rejectedOperationIdData = await CreateAmbiguousAttemptAsync(con, tx, poolId, now.AddMinutes(2),
                sendShape: PayoutSendShapes.AsyncOperation);
            var rejectedOperationId = await asyncService.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(rejectedOperationIdData, now.AddMinutes(3), PayoutExternalConfirmationKinds.OperationId,
                    "operationid-not-final"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, rejectedOperationId.Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview,
                await GetAttemptStateAsync(con, tx, rejectedOperationIdData.Attempt.Id));
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, rejectedOperationIdData.Batch.Id,
                rejectedOperationIdData.Attempt.Id,
                PayoutExternalConfirmationKinds.OperationId, "operationid-not-final"));

            var rejectedWalletAckData = await CreateAmbiguousAttemptAsync(con, tx, poolId, now.AddMinutes(4),
                sendShape: PayoutSendShapes.AsyncOperation);
            var rejectedWalletAck = await asyncService.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(rejectedWalletAckData, now.AddMinutes(5), PayoutExternalConfirmationKinds.WalletAck,
                    "wallet-ack-not-final"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, rejectedWalletAck.Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview,
                await GetAttemptStateAsync(con, tx, rejectedWalletAckData.Attempt.Id));
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, rejectedWalletAckData.Batch.Id,
                rejectedWalletAckData.Attempt.Id,
                PayoutExternalConfirmationKinds.WalletAck, "wallet-ack-not-final"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_MetadataMismatchRejectsAcceptedEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_metadata_mismatch");
            var now = UtcNow();
            var data = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);
            var mismatchService = NewService(TxIdProfile() with { AdapterId = "different-handler" });

            var result = await mismatchService.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(data, now.AddMinutes(1), PayoutExternalConfirmationKinds.TxId, "txid-reviewed"),
                Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, result.Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, data.Attempt.Id));
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, data.Batch.Id, data.Attempt.Id,
                PayoutExternalConfirmationKinds.TxId, "txid-reviewed"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_ConflictingExistingFinalEvidenceRejectsAcceptedEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_conflicting_evidence");
            var now = UtcNow();
            var data = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);
            await InsertConfirmationAsync(con, tx, data, PayoutExternalConfirmationKinds.TxId, "txid-existing",
                now.AddMinutes(1));

            var result = await service.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(data, now.AddMinutes(2), PayoutExternalConfirmationKinds.TxId, "txid-reviewed"),
                Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, result.Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, data.Attempt.Id));
            Assert.Equal(1, await CountConfirmationsByKindAsync(con, tx, data.Batch.Id, data.Attempt.Id,
                PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, data.Batch.Id, data.Attempt.Id,
                PayoutExternalConfirmationKinds.TxId, "txid-reviewed"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_ExistingDifferentFinalEvidenceKindRejectsAcceptedEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_cross_kind_conflict");
            var now = UtcNow();

            var txIdProfileData = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);
            await InsertConfirmationAsync(con, tx, txIdProfileData, PayoutExternalConfirmationKinds.RawHash,
                "rawhash-existing", now.AddMinutes(1));
            var txIdProfileResult = await service.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(txIdProfileData, now.AddMinutes(2), PayoutExternalConfirmationKinds.TxId,
                    "txid-reviewed"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, txIdProfileResult.Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview,
                await GetAttemptStateAsync(con, tx, txIdProfileData.Attempt.Id));
            Assert.Equal(1, await CountConfirmationsAsync(con, tx, txIdProfileData.Batch.Id,
                txIdProfileData.Attempt.Id, PayoutExternalConfirmationKinds.RawHash, "rawhash-existing"));
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, txIdProfileData.Batch.Id,
                txIdProfileData.Attempt.Id, PayoutExternalConfirmationKinds.TxId, "txid-reviewed"));

            var rawHashProfileData = await CreateAmbiguousAttemptAsync(con, tx, poolId, now.AddMinutes(3));
            await InsertConfirmationAsync(con, tx, rawHashProfileData, PayoutExternalConfirmationKinds.TxId,
                "txid-existing", now.AddMinutes(4));
            var rawHashService = NewService(RawHashProfile());
            var rawHashProfileResult = await rawHashService.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(rawHashProfileData, now.AddMinutes(5), PayoutExternalConfirmationKinds.RawHash,
                    "rawhash-reviewed"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, rawHashProfileResult.Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview,
                await GetAttemptStateAsync(con, tx, rawHashProfileData.Attempt.Id));
            Assert.Equal(1, await CountConfirmationsAsync(con, tx, rawHashProfileData.Batch.Id,
                rawHashProfileData.Attempt.Id, PayoutExternalConfirmationKinds.TxId, "txid-existing"));
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, rawHashProfileData.Batch.Id,
                rawHashProfileData.Attempt.Id, PayoutExternalConfirmationKinds.RawHash, "rawhash-reviewed"));

            var asyncProfileData = await CreateAmbiguousAttemptAsync(con, tx, poolId, now.AddMinutes(6),
                sendShape: PayoutSendShapes.AsyncOperation);
            await InsertConfirmationAsync(con, tx, asyncProfileData, PayoutExternalConfirmationKinds.RawHash,
                "rawhash-existing-async", now.AddMinutes(7));
            var asyncService = NewService(AsyncOperationProfile());
            var asyncProfileResult = await asyncService.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(asyncProfileData, now.AddMinutes(8), PayoutExternalConfirmationKinds.TxId,
                    "txid-reviewed-async"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, asyncProfileResult.Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview,
                await GetAttemptStateAsync(con, tx, asyncProfileData.Attempt.Id));
            Assert.Equal(1, await CountConfirmationsAsync(con, tx, asyncProfileData.Batch.Id,
                asyncProfileData.Attempt.Id, PayoutExternalConfirmationKinds.RawHash, "rawhash-existing-async"));
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, asyncProfileData.Batch.Id,
                asyncProfileData.Attempt.Id, PayoutExternalConfirmationKinds.TxId, "txid-reviewed-async"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_ExistingSameFinalEvidencePairAllowsAcceptedEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_same_evidence");
            var now = UtcNow();
            var data = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);
            await InsertConfirmationAsync(con, tx, data, PayoutExternalConfirmationKinds.TxId, "txid-reviewed",
                now.AddMinutes(1));

            var result = await service.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(data, now.AddMinutes(2), PayoutExternalConfirmationKinds.TxId, "txid-reviewed"),
                Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.Accepted, result.Status);
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, tx, data.Attempt.Id));
            Assert.Equal(1, await CountConfirmationsAsync(con, tx, data.Batch.Id, data.Attempt.Id,
                PayoutExternalConfirmationKinds.TxId, "txid-reviewed"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_UnsupportedOrNotReadyProfileRejectsAcceptedEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_profile_not_ready");
            var now = UtcNow();
            var unsupportedData = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);
            var unsupportedService = new PayoutAmbiguousReviewService(repo,
                new FixedProfileResolver(PayoutProfileResolution.Unsupported("coin not configured")));

            var unsupported = await unsupportedService.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(unsupportedData, now.AddMinutes(1), PayoutExternalConfirmationKinds.TxId,
                    "txid-unsupported"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, unsupported.Status);
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, unsupportedData.Batch.Id,
                unsupportedData.Attempt.Id, PayoutExternalConfirmationKinds.TxId, "txid-unsupported"));

            var notReadyData = await CreateAmbiguousAttemptAsync(con, tx, poolId, now.AddMinutes(2));
            var notReadyService = NewService(TxIdProfile() with
            {
                ReservationReady = false,
                NotReadyReason = "not ready"
            });

            var notReady = await notReadyService.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(notReadyData, now.AddMinutes(3), PayoutExternalConfirmationKinds.TxId,
                    "txid-not-ready"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, notReady.Status);
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, notReadyData.Batch.Id,
                notReadyData.Attempt.Id, PayoutExternalConfirmationKinds.TxId, "txid-not-ready"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_ProvenNoAcceptReturnsIntentAndBatchToReserved()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_no_accept");
            var now = UtcNow();
            var data = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);
            await InsertBalanceAsync(con, tx, poolId, "addr-a", 10m, now);
            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);
            var balanceAmountBefore = await SumBalancesAsync(con, tx, poolId);

            var result = await service.ReviewAmbiguousAttemptAsync(con, tx, NewNoAcceptRequest(data, now.AddMinutes(1)),
                Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.FailedNoAccept, result.Status);
            Assert.Equal(PayoutSendAttemptStates.FailedNoAccept, await GetAttemptStateAsync(con, tx, data.Attempt.Id));
            Assert.Equal(PayoutAttemptIntentStates.FailedNoAccept,
                await GetAttemptIntentStateAsync(con, tx, data.Attempt.Id, data.Batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal(PayoutBatchStates.Reserved, await GetBatchStateAsync(con, tx, data.Batch.Id));
            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(balanceAmountBefore, await SumBalancesAsync(con, tx, poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_IneligibleAttemptReturnsNoOp()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_ineligible");
            var now = UtcNow();
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "review-ineligible", now, batch.Intents[0].Id);

            var result = await service.ReviewAmbiguousAttemptAsync(con, tx, NewAcceptedRequest(new TestPayoutData(batch, attempt),
                now.AddMinutes(1), PayoutExternalConfirmationKinds.TxId, "txid-ineligible"), Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, result.Status);
            Assert.Equal(PayoutBatchStates.Reserved, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, tx, attempt.Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, batch.Id, attempt.Id,
                PayoutExternalConfirmationKinds.TxId, "txid-ineligible"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_ProvenNoAcceptRejectsMismatchedBatchId()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_no_accept_mismatch");
            var now = UtcNow();
            var data = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);
            var wrongBatchId = await InsertInactiveBatchAsync(con, tx, poolId, now.AddSeconds(1));
            await InsertBalanceAsync(con, tx, poolId, "addr-a", 10m, now);
            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);
            var balanceAmountBefore = await SumBalancesAsync(con, tx, poolId);

            var result = await service.ReviewAmbiguousAttemptAsync(con, tx,
                NewNoAcceptRequest(data, now.AddMinutes(1)) with { BatchId = wrongBatchId }, Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, result.Status);
            Assert.Equal(wrongBatchId, result.BatchId);
            Assert.Equal(data.Attempt.Id, result.AttemptId);
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, tx, data.Batch.Id));
            Assert.Equal(PayoutBatchStates.Cancelled, await GetBatchStateAsync(con, tx, wrongBatchId));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, data.Attempt.Id));
            Assert.Equal(PayoutAttemptIntentStates.AmbiguousRequiresReview,
                await GetAttemptIntentStateAsync(con, tx, data.Attempt.Id, data.Batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(balanceAmountBefore, await SumBalancesAsync(con, tx, poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_AcceptedWithEvidenceRejectsMismatchedBatchId()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_accept_mismatch");
            var now = UtcNow();
            var data = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);
            var wrongBatchId = await InsertInactiveBatchAsync(con, tx, poolId, now.AddSeconds(1));

            var result = await service.ReviewAmbiguousAttemptAsync(con, tx,
                NewAcceptedRequest(data, now.AddMinutes(1), PayoutExternalConfirmationKinds.TxId, "txid-mismatch") with
                {
                    BatchId = wrongBatchId
                }, Ct);

            Assert.Equal(PayoutAmbiguousReviewStatus.AttemptNotEligible, result.Status);
            Assert.Equal(wrongBatchId, result.BatchId);
            Assert.Equal(data.Attempt.Id, result.AttemptId);
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, tx, data.Batch.Id));
            Assert.Equal(PayoutBatchStates.Cancelled, await GetBatchStateAsync(con, tx, wrongBatchId));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, data.Attempt.Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal(0, await CountConfirmationsAsync(con, tx, data.Batch.Id, data.Attempt.Id,
                PayoutExternalConfirmationKinds.TxId, "txid-mismatch"));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_RejectsInvalidAcceptedAndNoAcceptRequests()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_review_invalid");
            var now = UtcNow();
            var data = await CreateAmbiguousAttemptAsync(con, tx, poolId, now);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ReviewAmbiguousAttemptAsync(con, tx, NewAcceptedRequest(data, now.AddMinutes(1),
                    PayoutExternalConfirmationKinds.TxId, "txid") with { Evidence = null }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ReviewAmbiguousAttemptAsync(con, tx, NewNoAcceptRequest(data, now.AddMinutes(1)) with
                {
                    ErrorCode = " "
                }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ReviewAmbiguousAttemptAsync(con, tx, NewNoAcceptRequest(data, now.AddMinutes(1)) with
                {
                    ErrorMessage = " "
                }, Ct));

            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, data.Attempt.Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ReviewAmbiguousAttemptAsync_RequiresTransactionAndValidArguments()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var request = new PayoutAmbiguousReviewRequest
            {
                PoolId = "pool",
                BatchId = 1,
                AttemptId = 1,
                Decision = PayoutAmbiguousReviewDecision.AcceptedWithEvidence,
                Evidence = NewEvidence(PayoutExternalConfirmationKinds.TxId, "txid"),
                ReviewedAt = UtcNow()
            };

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ReviewAmbiguousAttemptAsync(null, tx, request, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ReviewAmbiguousAttemptAsync(con, null, request, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ReviewAmbiguousAttemptAsync(con, tx, null, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ReviewAmbiguousAttemptAsync(con, tx, request with { PoolId = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.ReviewAmbiguousAttemptAsync(con, tx, request with { BatchId = 0 }, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.ReviewAmbiguousAttemptAsync(con, tx, request with { AttemptId = 0 }, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.ReviewAmbiguousAttemptAsync(con, tx, request with { Decision = (PayoutAmbiguousReviewDecision)999 }, Ct));
        });
    }

    private async Task<TestPayoutData> CreateAmbiguousAttemptAsync(NpgsqlConnection con, NpgsqlTransaction tx,
        string poolId, DateTime created, string sendShape = PayoutSendShapes.PerAddress)
    {
        var batch = await CreateBatchAsync(con, tx, poolId, created, sendShape, ("addr-a", 1m));
        var attempt = await CreateAttemptAsync(con, tx, batch, 1, $"review-ambiguous-{Guid.NewGuid():N}", created,
            batch.Intents[0].Id);

        await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, created.AddMinutes(1), Ct);
        await repo.MarkAttemptAmbiguousAsync(con, tx, attempt.Id, poolId, "requires_review", null,
            created.AddMinutes(2), Ct);

        return new TestPayoutData(batch, attempt);
    }

    private Task<PayoutBatch> CreateBatchAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId,
        DateTime created, params (string address, decimal amount)[] intents)
    {
        return CreateBatchAsync(con, tx, poolId, created, PayoutSendShapes.PerAddress, intents);
    }

    private Task<PayoutBatch> CreateBatchAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId,
        DateTime created, string sendShape, params (string address, decimal amount)[] intents)
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
            SendShape = sendShape,
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

    private static PayoutAmbiguousReviewRequest NewAcceptedRequest(TestPayoutData data, DateTime reviewedAt,
        string kind, string value)
    {
        return new PayoutAmbiguousReviewRequest
        {
            PoolId = data.Batch.PoolId,
            BatchId = data.Batch.Id,
            AttemptId = data.Attempt.Id,
            Decision = PayoutAmbiguousReviewDecision.AcceptedWithEvidence,
            Evidence = NewEvidence(kind, value),
            ReviewedAt = reviewedAt
        };
    }

    private static PayoutAmbiguousReviewRequest NewNoAcceptRequest(TestPayoutData data, DateTime reviewedAt)
    {
        return new PayoutAmbiguousReviewRequest
        {
            PoolId = data.Batch.PoolId,
            BatchId = data.Batch.Id,
            AttemptId = data.Attempt.Id,
            Decision = PayoutAmbiguousReviewDecision.ProvenNoAccept,
            ErrorCode = "reviewed_no_accept",
            ErrorMessage = null,
            ReviewedAt = reviewedAt
        };
    }

    private static PayoutAttemptEvidence NewEvidence(string kind, string value)
    {
        return new PayoutAttemptEvidence
        {
            Kind = kind,
            Value = value
        };
    }

    private async Task InsertConfirmationAsync(NpgsqlConnection con, NpgsqlTransaction tx, TestPayoutData data, string kind,
        string value, DateTime created)
    {
        await repo.InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
        {
            PoolId = data.Batch.PoolId,
            Coin = data.Batch.Coin,
            BatchId = data.Batch.Id,
            AttemptId = data.Attempt.Id,
            Kind = kind,
            Value = value,
            Created = created
        }, Ct);
    }

    private static PayoutAmbiguousReviewService NewService(PayoutProfile profile)
    {
        return new PayoutAmbiguousReviewService(new PayoutIntentRepository(),
            new FixedProfileResolver(PayoutProfileResolution.Resolved(profile)));
    }

    private static PayoutProfile TxIdProfile()
    {
        return new PayoutProfile
        {
            CoinKey = Coin,
            CoinFamily = CoinFamily,
            AdapterId = Handler,
            SendShape = PayoutSendShapes.PerAddress,
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
            SendShape = PayoutSendShapes.AsyncOperation,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId,
            RequiresOperationIdProvider = true,
            SupportsShieldedOperationTracking = true
        };
    }

    private static Task InsertBalanceAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId, string address,
        decimal amount, DateTime created)
    {
        return con.ExecuteAsync(@"INSERT INTO balances(poolid, address, amount, created, updated)
            VALUES(@poolid, @address, @amount, @created, @created)",
            new { poolid = poolId, address, amount, created }, tx);
    }

    private static Task<long> InsertInactiveBatchAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId,
        DateTime created)
    {
        return con.QuerySingleAsync<long>(@"INSERT INTO payout_batches(poolid, coin, coinfamily, handler, state, sendshape,
                recipientsethash, minimumamount, reservedamountsnapshot, intentcountsnapshot, errorcode, created, updated)
            VALUES(@poolid, @coin, @coinfamily, @handler, @state, @sendshape, @recipientsethash, 0, 1, 1, @errorcode, @created, @created)
            RETURNING id",
            new
            {
                poolid = poolId,
                coin = Coin,
                coinfamily = CoinFamily,
                handler = Handler,
                state = PayoutBatchStates.Cancelled,
                sendshape = PayoutSendShapes.PerAddress,
                recipientsethash = $"inactive-{Guid.NewGuid():N}",
                errorcode = "test_inactive_batch",
                created
            }, tx);
    }

    private static Task<string> GetBatchStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_batches WHERE id = @batchid",
            new { batchid = batchId }, tx);
    }

    private static Task<string> GetAttemptStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_send_attempts WHERE id = @attemptid",
            new { attemptid = attemptId }, tx);
    }

    private static Task<string> GetIntentStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long intentId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_intents WHERE id = @intentid",
            new { intentid = intentId }, tx);
    }

    private static Task<string> GetAttemptIntentStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId,
        long intentId)
    {
        return con.QuerySingleAsync<string>(@"SELECT state FROM payout_attempt_intents
            WHERE attemptid = @attemptid AND intentid = @intentid",
            new { attemptid = attemptId, intentid = intentId }, tx);
    }

    private static Task<int> CountConfirmationsAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId,
        long attemptId, string kind, string value)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_external_confirmations
            WHERE batchid = @batchid AND attemptid = @attemptid AND kind = @kind AND value = @value",
            new { batchid = batchId, attemptid = attemptId, kind, value }, tx);
    }

    private static Task<int> CountConfirmationsByKindAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId,
        long attemptId, string kind)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_external_confirmations
            WHERE batchid = @batchid AND attemptid = @attemptid AND kind = @kind",
            new { batchid = batchId, attemptid = attemptId, kind }, tx);
    }

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, IDbTransaction tx, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid", new { poolid = poolId }, tx);
    }

    private static Task<decimal> SumBalancesAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId)
    {
        return con.QuerySingleAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM balances WHERE poolid = @poolid",
            new { poolid = poolId }, tx);
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private record TestPayoutData(PayoutBatch Batch, PayoutSendAttempt Attempt);

    private sealed class FixedProfileResolver : IPayoutProfileResolver
    {
        public FixedProfileResolver(PayoutProfileResolution resolution)
        {
            this.resolution = resolution;
        }

        private readonly PayoutProfileResolution resolution;

        public PayoutProfileResolution Resolve(string coinKey) => resolution;
    }
}
