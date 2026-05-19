using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Model.Projections;
using HashStormCore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace HashStormCore.Tests.Persistence.Postgres;

public class PayoutIntentRepositoryTests : PostgresIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository repo = new();

    [PostgresIntegrationFact]
    public Task CreateReservedBatchAsync_CreatesBatchAndIntentsAtomically()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("create"), ("addr1", 1.25m), ("addr2", 2.75m));

            Assert.Equal(PayoutBatchStates.Reserved, batch.State);
            Assert.Equal(2, batch.Intents.Length);
            Assert.Equal(4.00m, batch.ReservedAmountSnapshot);

            Assert.Equal(1, await CountRowsAsync(con, tx, "payout_batches", batch.Id));
            Assert.Equal(2, await CountBatchRowsAsync(con, tx, "payout_intents", batch.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateReservedBatchAsync_RejectsInvalidIntentSnapshots()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("batch_invalid");

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.CreateReservedBatchAsync(con, tx, NewBatchRequest(poolId, 0, 0m), Array.Empty<CreatePayoutIntentRequest>(), Ct));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.CreateReservedBatchAsync(con, tx, NewBatchRequest(poolId, 2, 1m),
                    new[] { NewIntentRequest("addr1", 1m) }, Ct));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.CreateReservedBatchAsync(con, tx, NewBatchRequest(poolId, 1, 2m),
                    new[] { NewIntentRequest("addr1", 1m) }, Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptAsync_CreatesAttemptAndMappingsForReservedIntents()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("attempt_create"), ("addr1", 1m), ("addr2", 2m));

            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "requesthash-1", batch.Intents.Select(x => x.Id).ToArray());

            Assert.Equal(PayoutSendAttemptStates.Prepared, attempt.State);
            Assert.Equal(2, attempt.AttemptIntents.Length);
            Assert.Equal(2, await CountAttemptMappingsAsync(con, tx, attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptAsync_RejectsDuplicateIntentIdsAndInvalidStates()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("attempt_reject"), ("addr1", 1m), ("addr2", 2m));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                CreateAttemptAsync(con, tx, batch, 1, "requesthash-dup-ids", batch.Intents[0].Id, batch.Intents[0].Id));

            await con.ExecuteAsync("UPDATE payout_batches SET state = @state WHERE id = @batchid",
                new { state = PayoutBatchStates.Sending, batchid = batch.Id }, tx);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateAttemptAsync(con, tx, batch, 1, "requesthash-non-reserved-batch", batch.Intents[0].Id));

            var otherBatch = await CreateBatchAsync(con, tx, NewPoolId("attempt_reject_intent"), ("addr1", 1m));
            await con.ExecuteAsync("UPDATE payout_intents SET state = @state WHERE id = @intentid",
                new { state = PayoutIntentStates.Sending, intentid = otherBatch.Intents[0].Id }, tx);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateAttemptAsync(con, tx, otherBatch, 1, "requesthash-non-reserved-intent", otherBatch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptAsync_RejectsDuplicateRequestHashInSameBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("requesthash_same_batch"), ("addr1", 1m), ("addr2", 2m));

            await CreateAttemptAsync(con, tx, batch, 1, "same-requesthash", batch.Intents[0].Id);
            await Assert.ThrowsAsync<PostgresException>(() =>
                CreateAttemptAsync(con, tx, batch, 2, "same-requesthash", batch.Intents[1].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptAsync_AllowsSameRequestHashInDifferentBatches()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var first = await CreateBatchAsync(con, tx, NewPoolId("requesthash_pool_a"), ("addr1", 1m));
            var second = await CreateBatchAsync(con, tx, NewPoolId("requesthash_pool_b"), ("addr1", 1m));

            await CreateAttemptAsync(con, tx, first, 1, "shared-requesthash", first.Intents[0].Id);
            await CreateAttemptAsync(con, tx, second, 1, "shared-requesthash", second.Intents[0].Id);
        });
    }

    [PostgresIntegrationFact]
    public Task MarkAttemptSendingAsync_SerializesMultiplePreparedAttemptsInSameBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("multi_send"), ("addr1", 1m), ("addr2", 2m));
            var first = await CreateAttemptAsync(con, tx, batch, 1, "multi-send-1", batch.Intents[0].Id);
            var second = await CreateAttemptAsync(con, tx, batch, 2, "multi-send-2", batch.Intents[1].Id);

            Assert.True(await repo.MarkAttemptSendingAsync(con, tx, first.Id, batch.PoolId, UtcNow(), Ct));
            Assert.Equal(PayoutBatchStates.Sending, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.Sending, await GetAttemptStateAsync(con, tx, first.Id));
            Assert.Equal(PayoutIntentStates.Sending, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));

            Assert.False(await repo.MarkAttemptSendingAsync(con, tx, second.Id, batch.PoolId, UtcNow(), Ct));
            Assert.Equal(PayoutBatchStates.Sending, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, tx, second.Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, batch.Intents[1].Id));

            Assert.True(await repo.MarkAttemptAcceptedAsync(con, tx, first.Id, batch.PoolId,
                NewEvidence(PayoutExternalConfirmationKinds.TxId, "txid-multi-send-1"), UtcNow(), Ct));

            Assert.True(await repo.MarkAttemptSendingAsync(con, tx, second.Id, batch.PoolId, UtcNow(), Ct));
            Assert.Equal(PayoutBatchStates.Sending, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.Sending, await GetAttemptStateAsync(con, tx, second.Id));
            Assert.Equal(PayoutIntentStates.Sending, await GetIntentStateAsync(con, tx, batch.Intents[1].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkAttemptSendingAsync_IneligibleParentDoesNotPartiallyTransition()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var ineligibleStates = new[]
            {
                PayoutBatchStates.Submitted,
                PayoutBatchStates.AmbiguousRequiresReview,
                PayoutBatchStates.Cancelled,
                PayoutBatchStates.Settled,
                PayoutBatchStates.Failed
            };

            foreach(var state in ineligibleStates)
            {
                var batch = await CreateBatchAsync(con, tx, NewPoolId($"send_ineligible_{state}"), ("addr1", 1m));
                var attempt = await CreateAttemptAsync(con, tx, batch, 1, $"send-ineligible-{state}", batch.Intents[0].Id);

                await con.ExecuteAsync("UPDATE payout_batches SET state = @state WHERE id = @batchid",
                    new { state, batchid = batch.Id }, tx);

                Assert.False(await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, batch.PoolId, UtcNow(), Ct));
                Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, tx, attempt.Id));
                Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
            }
        });
    }

    [PostgresIntegrationFact]
    public Task MarkAttemptAcceptedAsync_TransitionsAttemptsAndSubmitsBatchOnlyWhenFinal()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("accepted");
            var batch = await CreateBatchAsync(con, tx, poolId, ("addr1", 1m), ("addr2", 2m));
            var first = await CreateAttemptAsync(con, tx, batch, 1, "accepted-1", batch.Intents[0].Id);
            var second = await CreateAttemptAsync(con, tx, batch, 2, "accepted-2", batch.Intents[1].Id);

            Assert.True(await repo.MarkAttemptSendingAsync(con, tx, first.Id, poolId, UtcNow(), Ct));
            Assert.True(await repo.MarkAttemptAcceptedAsync(con, tx, first.Id, poolId, NewEvidence(PayoutExternalConfirmationKinds.TxId, "txid-accepted-1"), UtcNow(), Ct));

            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, tx, first.Id));
            Assert.Equal(PayoutAttemptIntentStates.Accepted, await GetAttemptIntentStateAsync(con, tx, first.Id, batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
            Assert.Equal(PayoutBatchStates.Sending, await GetBatchStateAsync(con, tx, batch.Id));

            var paymentCount = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangeCount = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);
            var balanceCount = await CountPoolRowsAsync(con, tx, "balances", poolId);

            Assert.True(await repo.MarkAttemptSendingAsync(con, tx, second.Id, poolId, UtcNow(), Ct));
            Assert.True(await repo.MarkAttemptAcceptedAsync(con, tx, second.Id, poolId, NewEvidence(PayoutExternalConfirmationKinds.TxId, "txid-accepted-2"), UtcNow(), Ct));

            Assert.Equal(PayoutBatchStates.Submitted, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(paymentCount, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangeCount, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(balanceCount, await CountPoolRowsAsync(con, tx, "balances", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkAttemptAcceptedAsync_RejectsInvalidEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("accepted_invalid"), ("addr1", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "accepted-invalid", batch.Intents[0].Id);
            await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, batch.PoolId, UtcNow(), Ct);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.MarkAttemptAcceptedAsync(con, tx, attempt.Id, batch.PoolId, NewEvidence("unknown", "value"), UtcNow(), Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.MarkAttemptAcceptedAsync(con, tx, attempt.Id, batch.PoolId, NewEvidence(PayoutExternalConfirmationKinds.TxId, " "), UtcNow(), Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkAttemptAmbiguousAsync_MovesAttemptMappingsIntentsAndBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("ambiguous"), ("addr1", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "ambiguous", batch.Intents[0].Id);
            await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, batch.PoolId, UtcNow(), Ct);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.MarkAttemptAmbiguousAsync(con, tx, attempt.Id, batch.PoolId, " ", null, UtcNow(), Ct));

            Assert.True(await repo.MarkAttemptAmbiguousAsync(con, tx, attempt.Id, batch.PoolId, "timeout", null, UtcNow(), Ct));

            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, attempt.Id));
            Assert.Equal(PayoutAttemptIntentStates.AmbiguousRequiresReview, await GetAttemptIntentStateAsync(con, tx, attempt.Id, batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, tx, batch.Id));

            Assert.False(await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, batch.PoolId, UtcNow(), Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkAttemptAmbiguousAsync_PreservesPreparedSiblings()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("ambiguous_sibling");
            var batch = await CreateBatchAsync(con, tx, poolId, ("addr1", 1m), ("addr2", 2m));
            var target = await CreateAttemptAsync(con, tx, batch, 1, "ambiguous-sibling-target", batch.Intents[0].Id);
            var sibling = await CreateAttemptAsync(con, tx, batch, 2, "ambiguous-sibling-prepared", batch.Intents[1].Id);

            await repo.MarkAttemptSendingAsync(con, tx, target.Id, poolId, UtcNow(), Ct);

            var paymentCount = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangeCount = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);
            var balanceCount = await CountPoolRowsAsync(con, tx, "balances", poolId);

            Assert.True(await repo.MarkAttemptAmbiguousAsync(con, tx, target.Id, poolId, "timeout", null, UtcNow(), Ct));

            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, target.Id));
            Assert.Equal(PayoutAttemptIntentStates.AmbiguousRequiresReview,
                await GetAttemptIntentStateAsync(con, tx, target.Id, batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));

            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, tx, sibling.Id));
            Assert.Equal(PayoutAttemptIntentStates.Active,
                await GetAttemptIntentStateAsync(con, tx, sibling.Id, batch.Intents[1].Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, batch.Intents[1].Id));

            Assert.Equal(paymentCount, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangeCount, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(balanceCount, await CountPoolRowsAsync(con, tx, "balances", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkStaleBatchAmbiguousAsync_QuarantinesSingleSendingAttemptBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("stale_single"), ("addr1", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "stale-single", batch.Intents[0].Id);
            await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, batch.PoolId, UtcNow(), Ct);

            Assert.True(await repo.MarkStaleBatchAmbiguousAsync(con, tx, batch.Id, attempt.Id, batch.PoolId,
                "stale_sending_requires_review", null, UtcNow(), Ct));

            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, attempt.Id));
            Assert.Equal(PayoutAttemptIntentStates.AmbiguousRequiresReview,
                await GetAttemptIntentStateAsync(con, tx, attempt.Id, batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkStaleBatchAmbiguousAsync_QuarantinesPreparedAndSendingSiblings()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("stale_multi"),
                ("addr1", 1m), ("addr2", 2m), ("addr3", 3m));
            var stale = await CreateAttemptAsync(con, tx, batch, 1, "stale-multi-a", batch.Intents[0].Id);
            var sendingSibling = await CreateAttemptAsync(con, tx, batch, 2, "stale-multi-b", batch.Intents[1].Id);
            var preparedSibling = await CreateAttemptAsync(con, tx, batch, 3, "stale-multi-c", batch.Intents[2].Id);

            await repo.MarkAttemptSendingAsync(con, tx, stale.Id, batch.PoolId, UtcNow(), Ct);
            await SetAttemptStateAsync(con, tx, sendingSibling.Id, PayoutSendAttemptStates.Sending);
            await SetIntentStateAsync(con, tx, batch.Intents[1].Id, PayoutIntentStates.Sending);

            Assert.True(await repo.MarkStaleBatchAmbiguousAsync(con, tx, batch.Id, stale.Id, batch.PoolId,
                "stale_sending_requires_review", null, UtcNow(), Ct));

            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, stale.Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, sendingSibling.Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, preparedSibling.Id));

            Assert.Equal(PayoutAttemptIntentStates.AmbiguousRequiresReview,
                await GetAttemptIntentStateAsync(con, tx, stale.Id, batch.Intents[0].Id));
            Assert.Equal(PayoutAttemptIntentStates.AmbiguousRequiresReview,
                await GetAttemptIntentStateAsync(con, tx, sendingSibling.Id, batch.Intents[1].Id));
            Assert.Equal(PayoutAttemptIntentStates.AmbiguousRequiresReview,
                await GetAttemptIntentStateAsync(con, tx, preparedSibling.Id, batch.Intents[2].Id));

            Assert.All(await GetIntentStatesAsync(con, tx, batch.Id), state =>
                Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, state));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkStaleBatchAmbiguousAsync_PreservesAcceptedSubmittedAndFailedSiblings()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("stale_preserve_final");
            var batch = await CreateBatchAsync(con, tx, poolId,
                ("addr-accepted", 1m), ("addr-stale", 2m), ("addr-failed-pre", 3m), ("addr-failed-no", 4m));
            var accepted = await CreateAttemptAsync(con, tx, batch, 1, "stale-preserve-accepted", batch.Intents[0].Id);
            var stale = await CreateAttemptAsync(con, tx, batch, 2, "stale-preserve-stale", batch.Intents[1].Id);
            var failedPreAccept = await CreateAttemptAsync(con, tx, batch, 3, "stale-preserve-failed-pre", batch.Intents[2].Id);
            var failedNoAccept = await CreateAttemptAsync(con, tx, batch, 4, "stale-preserve-failed-no", batch.Intents[3].Id);

            await repo.MarkAttemptSendingAsync(con, tx, accepted.Id, poolId, UtcNow(), Ct);
            await repo.MarkAttemptAcceptedAsync(con, tx, accepted.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.TxId, "txid-stale-preserve"), UtcNow(), Ct);
            await repo.MarkAttemptSendingAsync(con, tx, stale.Id, poolId, UtcNow(), Ct);
            await repo.MarkAttemptFailedPreAcceptAsync(con, tx, failedPreAccept.Id, poolId, "pre_accept", null, UtcNow(), Ct);
            await SetFailedNoAcceptStateAsync(con, tx, failedNoAccept.Id, batch.Intents[3].Id);

            var paymentCount = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangeCount = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);
            var balanceCount = await CountPoolRowsAsync(con, tx, "balances", poolId);

            Assert.True(await repo.MarkStaleBatchAmbiguousAsync(con, tx, batch.Id, stale.Id, poolId,
                "stale_sending_requires_review", null, UtcNow(), Ct));

            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, tx, accepted.Id));
            Assert.Equal(PayoutAttemptIntentStates.Accepted,
                await GetAttemptIntentStateAsync(con, tx, accepted.Id, batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));

            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, stale.Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, batch.Intents[1].Id));

            Assert.Equal(PayoutSendAttemptStates.FailedPreAccept, await GetAttemptStateAsync(con, tx, failedPreAccept.Id));
            Assert.Equal(PayoutAttemptIntentStates.FailedPreAccept,
                await GetAttemptIntentStateAsync(con, tx, failedPreAccept.Id, batch.Intents[2].Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, batch.Intents[2].Id));

            Assert.Equal(PayoutSendAttemptStates.FailedNoAccept, await GetAttemptStateAsync(con, tx, failedNoAccept.Id));
            Assert.Equal(PayoutAttemptIntentStates.FailedNoAccept,
                await GetAttemptIntentStateAsync(con, tx, failedNoAccept.Id, batch.Intents[3].Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, batch.Intents[3].Id));

            Assert.Equal(paymentCount, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangeCount, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(balanceCount, await CountPoolRowsAsync(con, tx, "balances", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkStaleBatchAmbiguousAsync_IneligibleStatesReturnFalseWithoutMutation()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("stale_ineligible_batch"), ("addr1", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "stale-ineligible-batch", batch.Intents[0].Id);
            await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, batch.PoolId, UtcNow(), Ct);
            await con.ExecuteAsync("UPDATE payout_batches SET state = @state WHERE id = @batchid",
                new { state = PayoutBatchStates.Submitted, batchid = batch.Id }, tx);

            Assert.False(await repo.MarkStaleBatchAmbiguousAsync(con, tx, batch.Id, attempt.Id, batch.PoolId,
                "stale_sending_requires_review", null, UtcNow(), Ct));
            Assert.Equal(PayoutBatchStates.Submitted, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.Sending, await GetAttemptStateAsync(con, tx, attempt.Id));
            Assert.Equal(PayoutIntentStates.Sending, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));

            var preparedBatch = await CreateBatchAsync(con, tx, NewPoolId("stale_ineligible_attempt"), ("addr1", 1m));
            var preparedAttempt = await CreateAttemptAsync(con, tx, preparedBatch, 1, "stale-ineligible-attempt", preparedBatch.Intents[0].Id);
            await con.ExecuteAsync("UPDATE payout_batches SET state = @state WHERE id = @batchid",
                new { state = PayoutBatchStates.Sending, batchid = preparedBatch.Id }, tx);

            Assert.False(await repo.MarkStaleBatchAmbiguousAsync(con, tx, preparedBatch.Id, preparedAttempt.Id,
                preparedBatch.PoolId, "stale_sending_requires_review", null, UtcNow(), Ct));
            Assert.Equal(PayoutBatchStates.Sending, await GetBatchStateAsync(con, tx, preparedBatch.Id));
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, tx, preparedAttempt.Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, preparedBatch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkStaleBatchAmbiguousAsync_RequiresTransactionAndValidArguments()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.MarkStaleBatchAmbiguousAsync(null, tx, 1, 1, "pool", "stale", null, UtcNow(), Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.MarkStaleBatchAmbiguousAsync(con, null, 1, 1, "pool", "stale", null, UtcNow(), Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.MarkStaleBatchAmbiguousAsync(con, tx, 0, 1, "pool", "stale", null, UtcNow(), Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.MarkStaleBatchAmbiguousAsync(con, tx, 1, 0, "pool", "stale", null, UtcNow(), Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.MarkStaleBatchAmbiguousAsync(con, tx, 1, 1, " ", "stale", null, UtcNow(), Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.MarkStaleBatchAmbiguousAsync(con, tx, 1, 1, "pool", " ", null, UtcNow(), Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.MarkStaleBatchAmbiguousAsync(con, tx, 1, 1, "pool", "stale", " ", UtcNow(), Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkAttemptFailedPreAcceptAsync_ReturnsPreparedOrSendingIntentToReservedForRetry()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var preparedBatch = await CreateBatchAsync(con, tx, NewPoolId("failed_pre_prepared"), ("addr1", 1m));
            var preparedAttempt = await CreateAttemptAsync(con, tx, preparedBatch, 1, "failed-pre-prepared", preparedBatch.Intents[0].Id);

            Assert.True(await repo.MarkAttemptFailedPreAcceptAsync(con, tx, preparedAttempt.Id, preparedBatch.PoolId, "pre_accept", null, UtcNow(), Ct));
            Assert.Equal(PayoutSendAttemptStates.FailedPreAccept, await GetAttemptStateAsync(con, tx, preparedAttempt.Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, preparedBatch.Intents[0].Id));

            await CreateAttemptAsync(con, tx, preparedBatch, 2, "failed-pre-retry", preparedBatch.Intents[0].Id);

            var sendingBatch = await CreateBatchAsync(con, tx, NewPoolId("failed_pre_sending"), ("addr1", 1m));
            var sendingAttempt = await CreateAttemptAsync(con, tx, sendingBatch, 1, "failed-pre-sending", sendingBatch.Intents[0].Id);
            await repo.MarkAttemptSendingAsync(con, tx, sendingAttempt.Id, sendingBatch.PoolId, UtcNow(), Ct);

            Assert.True(await repo.MarkAttemptFailedPreAcceptAsync(con, tx, sendingAttempt.Id, sendingBatch.PoolId, "pre_accept", null, UtcNow(), Ct));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, sendingBatch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkAttemptFailedNoAcceptAsync_OnlyFromAmbiguousAndReturnsIntentToReserved()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("failed_no_accept"), ("addr1", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "failed-no-accept", batch.Intents[0].Id);
            await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, batch.PoolId, UtcNow(), Ct);

            Assert.False(await repo.MarkAttemptFailedNoAcceptAsync(con, tx, attempt.Id, batch.PoolId, "no_accept", null, UtcNow(), Ct));

            Assert.True(await repo.MarkAttemptAmbiguousAsync(con, tx, attempt.Id, batch.PoolId, "timeout", null, UtcNow(), Ct));
            Assert.True(await repo.MarkAttemptFailedNoAcceptAsync(con, tx, attempt.Id, batch.PoolId, "no_accept", null, UtcNow(), Ct));

            Assert.Equal(PayoutSendAttemptStates.FailedNoAccept, await GetAttemptStateAsync(con, tx, attempt.Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));

            await CreateAttemptAsync(con, tx, batch, 2, "failed-no-accept-retry", batch.Intents[0].Id);
        });
    }

    [PostgresIntegrationFact]
    public Task MarkBatchCancelledAsync_CancelsOnlyReservedBatchWithoutAttempts()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("cancel"), ("addr1", 1m), ("addr2", 2m));

            Assert.True(await repo.MarkBatchCancelledAsync(con, tx, batch.Id, batch.PoolId, "operator_cancelled", null, UtcNow(), Ct));
            Assert.Equal(PayoutBatchStates.Cancelled, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.All(await GetIntentStatesAsync(con, tx, batch.Id), state => Assert.Equal(PayoutIntentStates.Cancelled, state));

            var blockedBatch = await CreateBatchAsync(con, tx, NewPoolId("cancel_blocked"), ("addr1", 1m));
            await CreateAttemptAsync(con, tx, blockedBatch, 1, "cancel-blocked", blockedBatch.Intents[0].Id);

            Assert.False(await repo.MarkBatchCancelledAsync(con, tx, blockedBatch.Id, blockedBatch.PoolId, "operator_cancelled", null, UtcNow(), Ct));
            Assert.Equal(PayoutBatchStates.Reserved, await GetBatchStateAsync(con, tx, blockedBatch.Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, blockedBatch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task InsertExternalConfirmationAsync_ValidatesKindsAndValues()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("confirmation"), ("addr1", 1m));
            var kinds = new[]
            {
                PayoutExternalConfirmationKinds.TxId,
                PayoutExternalConfirmationKinds.OperationId,
                PayoutExternalConfirmationKinds.WalletAck,
                PayoutExternalConfirmationKinds.RawHash
            };

            foreach(var kind in kinds)
            {
                var confirmation = await repo.InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
                {
                    PoolId = batch.PoolId,
                    Coin = batch.Coin,
                    BatchId = batch.Id,
                    Kind = kind,
                    Value = $"{kind}-value-{Guid.NewGuid():N}",
                    Created = UtcNow()
                }, Ct);

                Assert.True(confirmation.Id > 0);
                Assert.Equal(kind, confirmation.Kind);
            }

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.InsertExternalConfirmationAsync(con, tx, NewConfirmation(batch, "placeholder", "value"), Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.InsertExternalConfirmationAsync(con, tx, NewConfirmation(batch, "unknown", "value"), Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.InsertExternalConfirmationAsync(con, tx, NewConfirmation(batch, PayoutExternalConfirmationKinds.TxId, " "), Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task GetBalanceProjectionsAsync_ComputesReservedAmbiguousAndAvailableWithoutMutatingBalances()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("projection");
            var created = UtcNow();

            await InsertBalanceAsync(con, tx, poolId, "addr-reserved", 10m, created);
            await InsertBalanceAsync(con, tx, poolId, "addr-sending", 20m, created);
            await InsertBalanceAsync(con, tx, poolId, "addr-submitted", 30m, created);
            await InsertBalanceAsync(con, tx, poolId, "addr-ambiguous", 40m, created);
            await InsertBalanceAsync(con, tx, poolId, "addr-clear", 50m, created);

            var batch = await CreateBatchAsync(con, tx, poolId,
                ("addr-reserved", 1m),
                ("addr-sending", 2m),
                ("addr-submitted", 3m),
                ("addr-ambiguous", 4m));

            await SetIntentStateAsync(con, tx, batch.Intents.Single(x => x.Address == "addr-sending").Id, PayoutIntentStates.Sending);
            await SetIntentStateAsync(con, tx, batch.Intents.Single(x => x.Address == "addr-submitted").Id, PayoutIntentStates.Submitted);
            await SetIntentStateAsync(con, tx, batch.Intents.Single(x => x.Address == "addr-ambiguous").Id, PayoutIntentStates.AmbiguousRequiresReview);

            var before = await SumBalancesAsync(con, tx, poolId);
            var projections = (await repo.GetBalanceProjectionsAsync(con, poolId, Ct)).ToDictionary(x => x.Address);

            AssertProjection(projections["addr-reserved"], 10m, 1m, 0m, 9m);
            AssertProjection(projections["addr-sending"], 20m, 2m, 0m, 18m);
            AssertProjection(projections["addr-submitted"], 30m, 3m, 0m, 27m);
            AssertProjection(projections["addr-ambiguous"], 40m, 0m, 4m, 36m);
            AssertProjection(projections["addr-clear"], 50m, 0m, 0m, 50m);

            Assert.Equal(before, await SumBalancesAsync(con, tx, poolId));
        });
    }

    private Task<PayoutBatch> CreateBatchAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId,
        params (string Address, decimal Amount)[] intents)
    {
        var requestIntents = intents
            .Select(x => NewIntentRequest(x.Address, x.Amount))
            .ToArray();

        return repo.CreateReservedBatchAsync(con, tx, NewBatchRequest(poolId, requestIntents.Length, requestIntents.Sum(x => x.Amount)),
            requestIntents, Ct);
    }

    private Task<PayoutSendAttempt> CreateAttemptAsync(NpgsqlConnection con, NpgsqlTransaction tx, PayoutBatch batch,
        int attemptNo, string requestHash, params long[] intentIds)
    {
        var selected = batch.Intents.Where(x => intentIds.Contains(x.Id)).ToArray();
        return repo.CreateSendAttemptAsync(con, tx, new CreatePayoutSendAttemptRequest
        {
            BatchId = batch.Id,
            PoolId = batch.PoolId,
            Coin = batch.Coin,
            AttemptNo = attemptNo,
            Method = "send",
            RequestHash = requestHash,
            RequestSummary = $"attempt-{attemptNo}",
            RecipientCount = intentIds.Length,
            AmountSnapshot = selected.Sum(x => x.Amount),
            Created = UtcNow()
        }, intentIds, Ct);
    }

    private static CreatePayoutBatchRequest NewBatchRequest(string poolId, int intentCount, decimal reservedAmount)
    {
        return new CreatePayoutBatchRequest
        {
            PoolId = poolId,
            Coin = Coin,
            CoinFamily = CoinFamily,
            Handler = Handler,
            SendShape = PayoutSendShapes.PerAddress,
            RecipientSetHash = $"recipient-set-{Guid.NewGuid():N}",
            MinimumAmount = 0m,
            ReservedAmountSnapshot = reservedAmount,
            IntentCountSnapshot = intentCount,
            Created = UtcNow()
        };
    }

    private static CreatePayoutIntentRequest NewIntentRequest(string address, decimal amount)
    {
        return new CreatePayoutIntentRequest
        {
            Address = address,
            Amount = amount,
            BalanceSnapshotAmount = amount,
            BalanceSnapshotUpdated = UtcNow(),
            PaymentThreshold = 0m
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

    private static PayoutExternalConfirmation NewConfirmation(PayoutBatch batch, string kind, string value)
    {
        return new PayoutExternalConfirmation
        {
            PoolId = batch.PoolId,
            Coin = batch.Coin,
            BatchId = batch.Id,
            Kind = kind,
            Value = value,
            Created = UtcNow()
        };
    }

    private static Task<int> CountRowsAsync(NpgsqlConnection con, NpgsqlTransaction tx, string table, long id)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE id = @id", new { id }, tx);
    }

    private static Task<int> CountBatchRowsAsync(NpgsqlConnection con, NpgsqlTransaction tx, string table, long batchId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE batchid = @batchid", new { batchid = batchId }, tx);
    }

    private static Task<int> CountAttemptMappingsAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId)
    {
        return con.QuerySingleAsync<int>("SELECT COUNT(*) FROM payout_attempt_intents WHERE attemptid = @attemptid",
            new { attemptid = attemptId }, tx);
    }

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, NpgsqlTransaction tx, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid", new { poolid = poolId }, tx);
    }

    private static Task<string> GetBatchStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_batches WHERE id = @batchid", new { batchid = batchId }, tx);
    }

    private static Task<string> GetAttemptStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_send_attempts WHERE id = @attemptid", new { attemptid = attemptId }, tx);
    }

    private static Task<string> GetIntentStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long intentId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_intents WHERE id = @intentid", new { intentid = intentId }, tx);
    }

    private static Task<string> GetAttemptIntentStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId, long intentId)
    {
        return con.QuerySingleAsync<string>(
            "SELECT state FROM payout_attempt_intents WHERE attemptid = @attemptid AND intentid = @intentid",
            new { attemptid = attemptId, intentid = intentId }, tx);
    }

    private static async Task<string[]> GetIntentStatesAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId)
    {
        return (await con.QueryAsync<string>("SELECT state FROM payout_intents WHERE batchid = @batchid ORDER BY id",
            new { batchid = batchId }, tx)).ToArray();
    }

    private static Task SetIntentStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long intentId, string state)
    {
        return con.ExecuteAsync("UPDATE payout_intents SET state = @state WHERE id = @intentid",
            new { state, intentid = intentId }, tx);
    }

    private static Task SetAttemptStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId, string state)
    {
        return con.ExecuteAsync("UPDATE payout_send_attempts SET state = @state WHERE id = @attemptid",
            new { state, attemptid = attemptId }, tx);
    }

    private static async Task SetFailedNoAcceptStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId, long intentId)
    {
        await con.ExecuteAsync("UPDATE payout_send_attempts SET state = @state WHERE id = @attemptid",
            new { state = PayoutSendAttemptStates.FailedNoAccept, attemptid = attemptId }, tx);

        await con.ExecuteAsync(@"UPDATE payout_attempt_intents
            SET state = @state
            WHERE attemptid = @attemptid AND intentid = @intentid",
            new { state = PayoutAttemptIntentStates.FailedNoAccept, attemptid = attemptId, intentid = intentId }, tx);
    }

    private static Task InsertBalanceAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId, string address,
        decimal amount, DateTime created)
    {
        return con.ExecuteAsync(@"INSERT INTO balances(poolid, address, amount, created, updated)
            VALUES(@poolid, @address, @amount, @created, @created)",
            new { poolid = poolId, address, amount, created }, tx);
    }

    private static Task<decimal> SumBalancesAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId)
    {
        return con.QuerySingleAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM balances WHERE poolid = @poolid",
            new { poolid = poolId }, tx);
    }

    private static void AssertProjection(PayoutBalanceProjection actual,
        decimal total, decimal reserved, decimal ambiguous, decimal available)
    {
        Assert.Equal(total, actual.Total);
        Assert.Equal(reserved, actual.Reserved);
        Assert.Equal(ambiguous, actual.Ambiguous);
        Assert.Equal(available, actual.Available);
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }
}
