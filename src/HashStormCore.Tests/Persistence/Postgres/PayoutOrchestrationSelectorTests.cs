using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace HashStormCore.Tests.Persistence.Postgres;

public class PayoutOrchestrationSelectorTests : PostgresIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository payoutIntentRepo = new();
    private readonly PayoutSettlementRepository payoutSettlementRepo = new();

    [PostgresIntegrationFact]
    public Task GetReservedBatchesForPlanningAsync_SelectsReservedBatchWithoutAttempts()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var poolId = NewPoolId("planning_select");
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m));
            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var changesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);
            var balancesBefore = await CountPoolRowsAsync(con, tx, "balances", poolId);

            var candidates = await payoutIntentRepo.GetReservedBatchesForPlanningAsync(con, tx, poolId, 10, Ct);

            var candidate = Assert.Single(candidates);
            Assert.Equal(batch.Id, candidate.BatchId);
            Assert.Equal(poolId, candidate.PoolId);
            Assert.Equal(Coin, candidate.Coin);
            Assert.Equal(CoinFamily, candidate.CoinFamily);
            Assert.Equal(Handler, candidate.Handler);
            Assert.Equal(PayoutSendShapes.BatchMultiRecipient, candidate.SendShape);
            Assert.Equal(1, candidate.IntentCountSnapshot);
            Assert.Equal(1m, candidate.ReservedAmountSnapshot);
            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(changesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(balancesBefore, await CountPoolRowsAsync(con, tx, "balances", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task GetReservedBatchesForPlanningAsync_IgnoresBatchesWithAttemptsNonReservedAndOtherPools()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var withAttempt = await CreateBatchAsync(con, tx, NewPoolId("planning_attempt"), now, ("addr-a", 1m));
            await CreateAttemptAsync(con, tx, withAttempt, 1, $"planning-attempt-{Guid.NewGuid():N}", now,
                withAttempt.Intents[0].Id);

            var nonReserved = await CreateBatchAsync(con, tx, NewPoolId("planning_non_reserved"), now, ("addr-a", 1m));
            await con.ExecuteAsync("UPDATE payout_batches SET state = @state WHERE id = @batchid",
                new { state = PayoutBatchStates.Sending, batchid = nonReserved.Id }, tx);

            var otherPool = await CreateBatchAsync(con, tx, NewPoolId("planning_other_pool"), now, ("addr-a", 1m));

            Assert.Empty(await payoutIntentRepo.GetReservedBatchesForPlanningAsync(con, tx, withAttempt.PoolId, 10, Ct));
            Assert.Empty(await payoutIntentRepo.GetReservedBatchesForPlanningAsync(con, tx, nonReserved.PoolId, 10, Ct));
            Assert.Empty(await payoutIntentRepo.GetReservedBatchesForPlanningAsync(con, tx, NewPoolId("planning_empty"), 10, Ct));
            Assert.Single(await payoutIntentRepo.GetReservedBatchesForPlanningAsync(con, tx, otherPool.PoolId, 1, Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task GetReservedBatchesForPlanningAsync_RequiresTransactionAndValidArguments()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                payoutIntentRepo.GetReservedBatchesForPlanningAsync(null, tx, "pool", 1, Ct));
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                payoutIntentRepo.GetReservedBatchesForPlanningAsync(con, null, "pool", 1, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                payoutIntentRepo.GetReservedBatchesForPlanningAsync(con, tx, " ", 1, Ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                payoutIntentRepo.GetReservedBatchesForPlanningAsync(con, tx, "pool", 0, Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task GetAcceptedAttemptsForSettlementAsync_SelectsTxIdAndRawHashEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var txidData = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settlement_txid_selector"), now,
                PayoutExternalConfirmationKinds.TxId, $"txid-selector-{Guid.NewGuid():N}", ("addr-a", 1m));
            var rawHashData = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settlement_raw_selector"), now,
                PayoutExternalConfirmationKinds.RawHash, $"raw-selector-{Guid.NewGuid():N}", ("addr-a", 1m));
            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", txidData.Batch.PoolId);
            var changesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", txidData.Batch.PoolId);
            var balancesBefore = await CountPoolRowsAsync(con, tx, "balances", txidData.Batch.PoolId);

            var txidCandidates = await payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx,
                txidData.Batch.PoolId, 10, Ct);
            var rawHashCandidates = await payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx,
                rawHashData.Batch.PoolId, 10, Ct);

            Assert.Single(txidCandidates);
            Assert.Equal(txidData.Attempt.Id, txidCandidates[0].AttemptId);
            Assert.Single(rawHashCandidates);
            Assert.Equal(rawHashData.Attempt.Id, rawHashCandidates[0].AttemptId);
            Assert.Equal(1, rawHashCandidates[0].SubmittedIntentCount);
            Assert.Equal(1, rawHashCandidates[0].UnsettledSubmittedIntentCount);
            Assert.Equal(0, rawHashCandidates[0].SettledIntentCount);
            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", txidData.Batch.PoolId));
            Assert.Equal(changesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", txidData.Batch.PoolId));
            Assert.Equal(balancesBefore, await CountPoolRowsAsync(con, tx, "balances", txidData.Batch.PoolId));
        });
    }

    [PostgresIntegrationFact]
    public Task GetAcceptedAttemptsForSettlementAsync_IgnoresInsufficientAndConflictingEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            await AssertNoSettlementCandidateAsync(con, tx, PayoutExternalConfirmationKinds.OperationId,
                $"operation-selector-{Guid.NewGuid():N}", "settlement_operationid");
            await AssertNoSettlementCandidateAsync(con, tx, PayoutExternalConfirmationKinds.WalletAck,
                $"wallet-ack-selector-{Guid.NewGuid():N}", "settlement_wallet_ack");

            var conflicting = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settlement_conflict"), now,
                PayoutExternalConfirmationKinds.TxId, $"txid-conflict-{Guid.NewGuid():N}", ("addr-a", 1m));
            await payoutIntentRepo.InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
            {
                PoolId = conflicting.Batch.PoolId,
                Coin = conflicting.Batch.Coin,
                BatchId = conflicting.Batch.Id,
                AttemptId = conflicting.Attempt.Id,
                Kind = PayoutExternalConfirmationKinds.RawHash,
                Value = $"raw-conflict-{Guid.NewGuid():N}",
                Created = now.AddMinutes(3)
            }, Ct);

            Assert.Empty(await payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx,
                conflicting.Batch.PoolId, 10, Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task GetAcceptedAttemptsForSettlementAsync_IgnoresNonAcceptedAttemptsAndOtherPools()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var ambiguousPool = NewPoolId("settlement_ambiguous_attempt");
            var ambiguousBatch = await CreateBatchAsync(con, tx, ambiguousPool, now, ("addr-a", 1m));
            var ambiguousAttempt = await CreateAttemptAsync(con, tx, ambiguousBatch, 1,
                $"settlement-ambiguous-{Guid.NewGuid():N}", now, ambiguousBatch.Intents[0].Id);
            await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, ambiguousAttempt.Id, ambiguousPool, now.AddMinutes(1), Ct);
            await payoutIntentRepo.MarkAttemptAmbiguousAsync(con, tx, ambiguousAttempt.Id, ambiguousPool, "ambiguous", null,
                now.AddMinutes(2), Ct);

            var selected = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settlement_pool_filter"), now,
                PayoutExternalConfirmationKinds.TxId, $"txid-pool-filter-{Guid.NewGuid():N}", ("addr-a", 1m));

            Assert.Empty(await payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx, ambiguousPool, 10, Ct));
            Assert.Empty(await payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx, NewPoolId("settlement_empty"), 10, Ct));
            Assert.Single(await payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx,
                selected.Batch.PoolId, 10, Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task GetAcceptedAttemptsForSettlementAsync_IncludesAcceptedAttemptUnderAmbiguousBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var poolId = NewPoolId("settlement_ambiguous_batch");
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m), ("addr-b", 1m));
            var accepted = await CreateAttemptAsync(con, tx, batch, 1, $"accepted-ambiguous-batch-{Guid.NewGuid():N}",
                now, batch.Intents[0].Id);
            var ambiguous = await CreateAttemptAsync(con, tx, batch, 2, $"ambiguous-batch-{Guid.NewGuid():N}",
                now, batch.Intents[1].Id);

            await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, accepted.Id, poolId, now.AddMinutes(1), Ct);
            await payoutIntentRepo.MarkAttemptAcceptedAsync(con, tx, accepted.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.TxId, $"txid-ambiguous-batch-{Guid.NewGuid():N}"),
                now.AddMinutes(2), Ct);
            await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, ambiguous.Id, poolId, now.AddMinutes(3), Ct);
            await payoutIntentRepo.MarkAttemptAmbiguousAsync(con, tx, ambiguous.Id, poolId, "ambiguous", null,
                now.AddMinutes(4), Ct);

            var candidates = await payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx, poolId, 10, Ct);

            var candidate = Assert.Single(candidates);
            Assert.Equal(accepted.Id, candidate.AttemptId);
            Assert.Equal(batch.Id, candidate.BatchId);
        });
    }

    [PostgresIntegrationFact]
    public Task GetAcceptedAttemptsForSettlementAsync_RespectsLimitAndOrderingWithinBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var poolId = NewPoolId("settlement_limit_order");
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m), ("addr-b", 1m));
            var first = await CreateAttemptAsync(con, tx, batch, 1, $"settlement-limit-1-{Guid.NewGuid():N}", now,
                batch.Intents[0].Id);
            var second = await CreateAttemptAsync(con, tx, batch, 2, $"settlement-limit-2-{Guid.NewGuid():N}", now,
                batch.Intents[1].Id);

            await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, first.Id, poolId, now.AddMinutes(1), Ct);
            await payoutIntentRepo.MarkAttemptAcceptedAsync(con, tx, first.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.TxId, $"txid-limit-1-{Guid.NewGuid():N}"),
                now.AddMinutes(2), Ct);
            await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, second.Id, poolId, now.AddMinutes(3), Ct);
            await payoutIntentRepo.MarkAttemptAcceptedAsync(con, tx, second.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.TxId, $"txid-limit-2-{Guid.NewGuid():N}"),
                now.AddMinutes(4), Ct);

            var candidates = await payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx, poolId, 1, Ct);

            var candidate = Assert.Single(candidates);
            Assert.Equal(first.Id, candidate.AttemptId);
        });
    }

    [PostgresIntegrationFact]
    public Task GetAcceptedAttemptsForSettlementAsync_RequiresTransactionAndValidArguments()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(null, tx, "pool", 1, Ct));
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, null, "pool", 1, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx, " ", 1, Ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx, "pool", 0, Ct));
        });
    }

    private async Task AssertNoSettlementCandidateAsync(NpgsqlConnection con, NpgsqlTransaction tx, string evidenceKind,
        string evidenceValue, string suffix)
    {
        var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId(suffix), UtcNow(), evidenceKind, evidenceValue,
            ("addr-a", 1m));

        Assert.Empty(await payoutSettlementRepo.GetAcceptedAttemptsForSettlementAsync(con, tx, data.Batch.PoolId, 10, Ct));
    }

    private async Task<TestPayoutData> CreateAcceptedAttemptAsync(NpgsqlConnection con, NpgsqlTransaction tx,
        string poolId, DateTime created, string evidenceKind, string evidenceValue,
        params (string address, decimal amount)[] intents)
    {
        var batch = await CreateBatchAsync(con, tx, poolId, created, intents);
        var attempt = await CreateAttemptAsync(con, tx, batch, 1, $"settlement-candidate-{Guid.NewGuid():N}", created,
            batch.Intents.Select(x => x.Id).ToArray());
        await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, created.AddMinutes(1), Ct);
        await payoutIntentRepo.MarkAttemptAcceptedAsync(con, tx, attempt.Id, poolId,
            NewEvidence(evidenceKind, evidenceValue), created.AddMinutes(2), Ct);

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

        return payoutIntentRepo.CreateReservedBatchAsync(con, tx, new CreatePayoutBatchRequest
        {
            PoolId = poolId,
            Coin = Coin,
            CoinFamily = CoinFamily,
            Handler = Handler,
            SendShape = PayoutSendShapes.BatchMultiRecipient,
            RecipientSetHash = $"orchestration-recipient-set-{Guid.NewGuid():N}",
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

        return payoutIntentRepo.CreateSendAttemptAsync(con, tx, new CreatePayoutSendAttemptRequest
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

    private static PayoutAttemptEvidence NewEvidence(string kind, string value)
    {
        return new PayoutAttemptEvidence
        {
            Kind = kind,
            Value = value
        };
    }

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, IDbTransaction tx, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid", new { poolid = poolId }, tx);
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private record TestPayoutData(PayoutBatch Batch, PayoutSendAttempt Attempt);
}
