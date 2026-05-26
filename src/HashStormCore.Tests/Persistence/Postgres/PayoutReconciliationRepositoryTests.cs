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

public class PayoutReconciliationRepositoryTests : PostgresIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository repo = new();

    [PostgresIntegrationFact]
    public Task GetStaleSendingAttemptsForUpdateAsync_ReturnsOnlyStaleSendingAttemptsUnderSendingBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reconcile_stale");
            var otherPoolId = NewPoolId("reconcile_stale_other");
            var old = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var fresh = old.AddHours(1);
            var cutoff = old.AddMinutes(30);

            var batch = await CreateBatchAsync(con, tx, poolId, old, ("addr-a", 1m), ("addr-b", 2m), ("addr-c", 3m));
            var stale = await CreateAttemptAsync(con, tx, batch, 1, "stale-old", old, batch.Intents[0].Id);
            var freshAttempt = await CreateAttemptAsync(con, tx, batch, 2, "stale-fresh", old.AddSeconds(1), batch.Intents[1].Id);
            var preparedAttempt = await CreateAttemptAsync(con, tx, batch, 3, "stale-prepared", old.AddSeconds(2), batch.Intents[2].Id);

            await repo.MarkAttemptSendingAsync(con, tx, stale.Id, poolId, old, Ct);
            await SetAttemptStateAsync(con, tx, freshAttempt.Id, PayoutSendAttemptStates.Sending);
            await SetIntentStateAsync(con, tx, batch.Intents[1].Id, PayoutIntentStates.Sending);
            await SetAttemptUpdatedAsync(con, tx, stale.Id, old);
            await SetAttemptUpdatedAsync(con, tx, freshAttempt.Id, fresh);

            var otherBatch = await CreateBatchAsync(con, tx, otherPoolId, old, ("addr-a", 1m));
            var otherAttempt = await CreateAttemptAsync(con, tx, otherBatch, 1, "stale-other-pool", old, otherBatch.Intents[0].Id);
            await repo.MarkAttemptSendingAsync(con, tx, otherAttempt.Id, otherPoolId, old, Ct);
            await SetAttemptUpdatedAsync(con, tx, otherAttempt.Id, old);

            var results = await repo.GetStaleSendingAttemptsForUpdateAsync(con, tx, poolId, cutoff, 10, Ct);

            Assert.Equal(new[] { stale.Id }, results.Select(x => x.AttemptId).ToArray());
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, tx, preparedAttempt.Id));
            Assert.Equal(PayoutSendAttemptStates.Sending, results.Single().AttemptState);
            Assert.Equal(PayoutBatchStates.Sending, results.Single().BatchState);

            await SetBatchStateAsync(con, tx, batch.Id, PayoutBatchStates.Submitted);

            var ignoredByBatch = await repo.GetStaleSendingAttemptsForUpdateAsync(con, tx, poolId, cutoff, 10, Ct);
            Assert.Empty(ignoredByBatch);
        });
    }

    [PostgresIntegrationFact]
    public Task GetStaleSendingAttemptsForUpdateAsync_RespectsLimitAndOrdering()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reconcile_stale_order");
            var start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var batch = await CreateBatchAsync(con, tx, poolId, start, ("addr-a", 1m), ("addr-b", 2m), ("addr-c", 3m));
            var second = await CreateAttemptAsync(con, tx, batch, 1, "stale-order-second", start, batch.Intents[0].Id);
            var first = await CreateAttemptAsync(con, tx, batch, 2, "stale-order-first", start, batch.Intents[1].Id);
            var third = await CreateAttemptAsync(con, tx, batch, 3, "stale-order-third", start, batch.Intents[2].Id);

            await repo.MarkAttemptSendingAsync(con, tx, second.Id, poolId, start.AddMinutes(2), Ct);
            await SetAttemptStateAsync(con, tx, first.Id, PayoutSendAttemptStates.Sending);
            await SetIntentStateAsync(con, tx, batch.Intents[1].Id, PayoutIntentStates.Sending);
            await SetAttemptStateAsync(con, tx, third.Id, PayoutSendAttemptStates.Sending);
            await SetIntentStateAsync(con, tx, batch.Intents[2].Id, PayoutIntentStates.Sending);
            await SetAttemptUpdatedAsync(con, tx, second.Id, start.AddMinutes(2));
            await SetAttemptUpdatedAsync(con, tx, first.Id, start.AddMinutes(1));
            await SetAttemptUpdatedAsync(con, tx, third.Id, start.AddMinutes(3));

            var results = await repo.GetStaleSendingAttemptsForUpdateAsync(con, tx, poolId, start.AddHours(1), 2, Ct);

            Assert.Equal(new[] { first.Id, second.Id }, results.Select(x => x.AttemptId).ToArray());
        });
    }

    [PostgresIntegrationFact]
    public Task GetStaleSendingBatchesForUpdateAsync_ReturnsOldestStaleAttemptPerSendingBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reconcile_stale_batches");
            var start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var cutoff = start.AddMinutes(10);
            var batch = await CreateBatchAsync(con, tx, poolId, start, ("addr-a", 1m), ("addr-b", 2m), ("addr-c", 3m));
            var firstStale = await CreateAttemptAsync(con, tx, batch, 1, "stale-batch-first", start, batch.Intents[0].Id);
            var oldestStale = await CreateAttemptAsync(con, tx, batch, 2, "stale-batch-oldest", start.AddSeconds(1), batch.Intents[1].Id);
            var preparedSibling = await CreateAttemptAsync(con, tx, batch, 3, "stale-batch-prepared", start.AddSeconds(2), batch.Intents[2].Id);

            await repo.MarkAttemptSendingAsync(con, tx, firstStale.Id, poolId, start.AddMinutes(2), Ct);
            await SetAttemptUpdatedAsync(con, tx, firstStale.Id, start.AddMinutes(2));
            await SetAttemptStateAsync(con, tx, oldestStale.Id, PayoutSendAttemptStates.Sending);
            await SetIntentStateAsync(con, tx, batch.Intents[1].Id, PayoutIntentStates.Sending);
            await SetAttemptUpdatedAsync(con, tx, oldestStale.Id, start.AddMinutes(1));

            var candidates = await repo.GetStaleSendingBatchesForUpdateAsync(con, tx, poolId, cutoff, 10, Ct);

            var candidate = Assert.Single(candidates);
            Assert.Equal(batch.Id, candidate.BatchId);
            Assert.Equal(oldestStale.Id, candidate.StaleAttemptId);
            Assert.All(candidates, item =>
            {
                Assert.Equal(poolId, item.PoolId);
                Assert.Equal(PayoutBatchStates.Sending, item.BatchState);
                Assert.Equal(PayoutSendAttemptStates.Sending, item.AttemptState);
            });
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, tx, preparedSibling.Id));

            var limited = await repo.GetStaleSendingBatchesForUpdateAsync(con, tx, poolId, cutoff, 1, Ct);
            Assert.Equal(new[] { batch.Id }, limited.Select(x => x.BatchId).ToArray());
        });
    }

    [PostgresIntegrationFact]
    public Task GetAmbiguousAttemptsAsync_ReturnsOnlyAmbiguousAttemptsUnderAmbiguousBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reconcile_ambiguous");
            var now = UtcNow();
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m), ("addr-b", 2m));
            var ambiguous = await CreateAttemptAsync(con, tx, batch, 1, "ambiguous-returned", now, batch.Intents[0].Id);
            var prepared = await CreateAttemptAsync(con, tx, batch, 2, "ambiguous-ignored-prepared", now, batch.Intents[1].Id);

            await repo.MarkAttemptSendingAsync(con, tx, ambiguous.Id, poolId, now, Ct);
            await repo.MarkAttemptAmbiguousAsync(con, tx, ambiguous.Id, poolId, "timeout", null, now.AddMinutes(1), Ct);

            var results = await repo.GetAmbiguousAttemptsAsync(con, tx, poolId, 10, Ct);

            Assert.Equal(new[] { ambiguous.Id }, results.Select(x => x.AttemptId).ToArray());
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, results.Single().AttemptState);
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, results.Single().BatchState);
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, tx, prepared.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task GetAttemptsWithOperationIdAsync_ReturnsOnlyOperationIdEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reconcile_operationid");
            var now = UtcNow();
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m), ("addr-b", 2m), ("addr-c", 3m), ("addr-d", 4m));
            var operationIdAttempt = await CreateAttemptAsync(con, tx, batch, 1, "opid-returned", now, batch.Intents[0].Id);
            var txIdAttempt = await CreateAttemptAsync(con, tx, batch, 2, "opid-ignored-txid", now.AddSeconds(1), batch.Intents[1].Id);
            var walletAckAttempt = await CreateAttemptAsync(con, tx, batch, 3, "opid-ignored-wallet-ack", now.AddSeconds(2), batch.Intents[2].Id);
            var rawHashAttempt = await CreateAttemptAsync(con, tx, batch, 4, "opid-ignored-raw-hash", now.AddSeconds(3), batch.Intents[3].Id);

            await repo.MarkAttemptSendingAsync(con, tx, operationIdAttempt.Id, poolId, now, Ct);
            await repo.MarkAttemptAcceptedAsync(con, tx, operationIdAttempt.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.OperationId, "opid-123"), now.AddMinutes(1), Ct);

            await repo.MarkAttemptSendingAsync(con, tx, txIdAttempt.Id, poolId, now.AddMinutes(2), Ct);
            await repo.MarkAttemptAcceptedAsync(con, tx, txIdAttempt.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.TxId, "txid-123"), now.AddMinutes(3), Ct);

            await repo.MarkAttemptSendingAsync(con, tx, walletAckAttempt.Id, poolId, now.AddMinutes(4), Ct);
            await repo.MarkAttemptAcceptedAsync(con, tx, walletAckAttempt.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.WalletAck, "wallet-ack-123"), now.AddMinutes(5), Ct);

            await repo.MarkAttemptSendingAsync(con, tx, rawHashAttempt.Id, poolId, now.AddMinutes(6), Ct);
            await repo.MarkAttemptAcceptedAsync(con, tx, rawHashAttempt.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.RawHash, "rawhash-123"), now.AddMinutes(7), Ct);

            var results = await repo.GetAttemptsWithOperationIdAsync(con, tx, poolId, 10, Ct);

            Assert.Equal(new[] { operationIdAttempt.Id }, results.Select(x => x.AttemptId).ToArray());
            Assert.Equal("opid-123", results.Single().ExternalOperationId);
            Assert.Equal(CoinFamily, results.Single().CoinFamily);
            Assert.Equal(Handler, results.Single().Handler);
            Assert.Equal(PayoutSendShapes.BatchMultiRecipient, results.Single().SendShape);
            Assert.Equal(Method, results.Single().Method);
        });
    }

    [PostgresIntegrationFact]
    public Task GetAttemptConfirmationsAsync_ReturnsOnlyRequestedAttemptConfirmations()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reconcile_confirmations");
            var otherPoolId = NewPoolId("reconcile_confirmations_other");
            var now = UtcNow();
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m), ("addr-b", 2m));
            var first = await CreateAttemptAsync(con, tx, batch, 1, "confirmations-first", now, batch.Intents[0].Id);
            var second = await CreateAttemptAsync(con, tx, batch, 2, "confirmations-second", now, batch.Intents[1].Id);

            await InsertConfirmationAsync(con, tx, batch.Id, first.Id, batch.Intents[0].Id, poolId, Coin,
                PayoutExternalConfirmationKinds.OperationId, "opid-first", now);
            await InsertConfirmationAsync(con, tx, batch.Id, first.Id, null, poolId, Coin,
                PayoutExternalConfirmationKinds.TxId, "txid-first", now.AddSeconds(1));
            await InsertConfirmationAsync(con, tx, batch.Id, second.Id, null, poolId, Coin,
                PayoutExternalConfirmationKinds.RawHash, "rawhash-second", now.AddSeconds(2));

            var otherBatch = await CreateBatchAsync(con, tx, otherPoolId, now, ("addr-a", 1m));
            var otherAttempt = await CreateAttemptAsync(con, tx, otherBatch, 1, "confirmations-other", now, otherBatch.Intents[0].Id);
            await InsertConfirmationAsync(con, tx, otherBatch.Id, otherAttempt.Id, null, otherPoolId, Coin,
                PayoutExternalConfirmationKinds.OperationId, "opid-other", now);

            var confirmations = await repo.GetAttemptConfirmationsAsync(con, tx, batch.Id, first.Id, poolId, Ct);

            Assert.Equal(new[] { PayoutExternalConfirmationKinds.OperationId, PayoutExternalConfirmationKinds.TxId },
                confirmations.Select(x => x.Kind).ToArray());
            Assert.All(confirmations, confirmation =>
            {
                Assert.Equal(batch.Id, confirmation.BatchId);
                Assert.Equal(first.Id, confirmation.AttemptId);
                Assert.Equal(poolId, confirmation.PoolId);
            });
            Assert.DoesNotContain(confirmations, x => x.Value == "rawhash-second" || x.Value == "opid-other");
        });
    }

    [PostgresIntegrationFact]
    public Task ReconciliationReadsRequireTransactionsAndValidArguments()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetStaleSendingAttemptsForUpdateAsync(null, tx, "pool", UtcNow(), 1, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetStaleSendingAttemptsForUpdateAsync(con, null, "pool", UtcNow(), 1, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.GetStaleSendingAttemptsForUpdateAsync(con, tx, " ", UtcNow(), 1, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.GetStaleSendingAttemptsForUpdateAsync(con, tx, "pool", UtcNow(), 0, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetStaleSendingBatchesForUpdateAsync(null, tx, "pool", UtcNow(), 1, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetStaleSendingBatchesForUpdateAsync(con, null, "pool", UtcNow(), 1, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.GetStaleSendingBatchesForUpdateAsync(con, tx, " ", UtcNow(), 1, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.GetStaleSendingBatchesForUpdateAsync(con, tx, "pool", UtcNow(), 0, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetAmbiguousAttemptsAsync(con, null, "pool", 1, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.GetAmbiguousAttemptsAsync(con, tx, " ", 1, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.GetAmbiguousAttemptsAsync(con, tx, "pool", 0, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetAttemptsWithOperationIdAsync(con, null, "pool", 1, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.GetAttemptsWithOperationIdAsync(con, tx, " ", 1, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.GetAttemptsWithOperationIdAsync(con, tx, "pool", 0, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetAttemptConfirmationsAsync(con, null, 1, 1, "pool", Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.GetAttemptConfirmationsAsync(con, tx, 0, 1, "pool", Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.GetAttemptConfirmationsAsync(con, tx, 1, 0, "pool", Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.GetAttemptConfirmationsAsync(con, tx, 1, 1, " ", Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconciliationReadsDoNotMutateAccountingOrBalances()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reconcile_no_mutation");
            var now = UtcNow();
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m), ("addr-b", 2m));
            var first = await CreateAttemptAsync(con, tx, batch, 1, "no-mutation-first", now, batch.Intents[0].Id);
            var second = await CreateAttemptAsync(con, tx, batch, 2, "no-mutation-second", now, batch.Intents[1].Id);

            await repo.MarkAttemptSendingAsync(con, tx, first.Id, poolId, now, Ct);
            await repo.MarkAttemptAcceptedAsync(con, tx, first.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.OperationId, "opid-no-mutation"), now.AddMinutes(1), Ct);
            await repo.MarkAttemptSendingAsync(con, tx, second.Id, poolId, now.AddMinutes(2), Ct);

            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);
            var balancesBefore = await CountPoolRowsAsync(con, tx, "balances", poolId);

            await repo.GetStaleSendingAttemptsForUpdateAsync(con, tx, poolId, now.AddHours(1), 10, Ct);
            await repo.GetAttemptsWithOperationIdAsync(con, tx, poolId, 10, Ct);
            await repo.GetAttemptConfirmationsAsync(con, tx, batch.Id, first.Id, poolId, Ct);

            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(balancesBefore, await CountPoolRowsAsync(con, tx, "balances", poolId));
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, tx, first.Id));
            Assert.Equal(PayoutSendAttemptStates.Sending, await GetAttemptStateAsync(con, tx, second.Id));
        });
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

    private static PayoutAttemptEvidence NewEvidence(string kind, string value)
    {
        return new PayoutAttemptEvidence
        {
            Kind = kind,
            Value = value
        };
    }

    private static Task InsertConfirmationAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId, long attemptId,
        long? intentId, string poolId, string coin, string kind, string value, DateTime created)
    {
        return con.ExecuteAsync(@"INSERT INTO payout_external_confirmations(poolid, coin, batchid, attemptid, intentid, kind, value, created)
            VALUES(@poolid, @coin, @batchid, @attemptid, @intentid, @kind, @value, @created)",
            new { poolid = poolId, coin, batchid = batchId, attemptid = attemptId, intentid = intentId, kind, value, created }, tx);
    }

    private static Task SetAttemptUpdatedAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId, DateTime updated)
    {
        return con.ExecuteAsync("UPDATE payout_send_attempts SET updated = @updated WHERE id = @attemptid",
            new { attemptid = attemptId, updated }, tx);
    }

    private static Task SetAttemptStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId, string state)
    {
        return con.ExecuteAsync("UPDATE payout_send_attempts SET state = @state WHERE id = @attemptid",
            new { attemptid = attemptId, state }, tx);
    }

    private static Task SetIntentStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long intentId, string state)
    {
        return con.ExecuteAsync("UPDATE payout_intents SET state = @state WHERE id = @intentid",
            new { intentid = intentId, state }, tx);
    }

    private static Task SetBatchStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId, string state)
    {
        return con.ExecuteAsync("UPDATE payout_batches SET state = @state WHERE id = @batchid",
            new { batchid = batchId, state }, tx);
    }

    private static Task<string> GetAttemptStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_send_attempts WHERE id = @attemptid",
            new { attemptid = attemptId }, tx);
    }

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, IDbTransaction tx, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid", new { poolid = poolId }, tx);
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }
}
