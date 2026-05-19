using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HashStormCore.Payments;
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
        service = new PayoutAmbiguousReviewService(repo);
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
                now.AddMinutes(2), PayoutExternalConfirmationKinds.RawHash, "rawhash-reviewed"), Ct);

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
        string poolId, DateTime created)
    {
        var batch = await CreateBatchAsync(con, tx, poolId, created, ("addr-a", 1m));
        var attempt = await CreateAttemptAsync(con, tx, batch, 1, $"review-ambiguous-{Guid.NewGuid():N}", created,
            batch.Intents[0].Id);

        await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, created.AddMinutes(1), Ct);
        await repo.MarkAttemptAmbiguousAsync(con, tx, attempt.Id, poolId, "requires_review", null,
            created.AddMinutes(2), Ct);

        return new TestPayoutData(batch, attempt);
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
            SendShape = PayoutSendShapes.PerAddress,
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
}
