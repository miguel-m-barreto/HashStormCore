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

public class PayoutSendExecutionRepositoryTests : PostgresIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository repo = new();

    [PostgresIntegrationFact]
    public Task GetPreparedAttemptsForExecutionAsync_ReturnsOnlyPreparedAttemptsForPool()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("execution_prepared");
            var otherPoolId = NewPoolId("execution_prepared_other");
            var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var batch = await CreateBatchAsync(con, tx, poolId, now,
                ("addr-a", 1m), ("addr-b", 2m), ("addr-c", 3m), ("addr-d", 4m), ("addr-e", 5m),
                ("addr-f", 6m), ("addr-g", 7m));

            var ignoredSending = await CreateAttemptAsync(con, tx, batch, 1, "execution-sending", now, batch.Intents[0].Id);
            var preparedSecond = await CreateAttemptAsync(con, tx, batch, 2, "execution-prepared-second", now.AddSeconds(20), batch.Intents[1].Id);
            var ignoredAccepted = await CreateAttemptAsync(con, tx, batch, 3, "execution-accepted", now.AddSeconds(30), batch.Intents[2].Id);
            var preparedFirst = await CreateAttemptAsync(con, tx, batch, 4, "execution-prepared-first", now.AddSeconds(10), batch.Intents[3].Id);
            var ignoredAmbiguous = await CreateAttemptAsync(con, tx, batch, 5, "execution-ambiguous", now.AddSeconds(40), batch.Intents[4].Id);
            var ignoredFailedPreAccept = await CreateAttemptAsync(con, tx, batch, 6, "execution-failed-pre-accept", now.AddSeconds(50), batch.Intents[5].Id);
            var ignoredFailedNoAccept = await CreateAttemptAsync(con, tx, batch, 7, "execution-failed-no-accept", now.AddSeconds(60), batch.Intents[6].Id);

            await SetAttemptStateAsync(con, tx, ignoredSending.Id, PayoutSendAttemptStates.Sending);
            await SetAttemptStateAsync(con, tx, ignoredAccepted.Id, PayoutSendAttemptStates.Accepted);
            await SetAttemptStateAsync(con, tx, ignoredAmbiguous.Id, PayoutSendAttemptStates.AmbiguousRequiresReview);
            await SetAttemptStateAsync(con, tx, ignoredFailedPreAccept.Id, PayoutSendAttemptStates.FailedPreAccept);
            await SetAttemptStateAsync(con, tx, ignoredFailedNoAccept.Id, PayoutSendAttemptStates.FailedNoAccept);

            var otherBatch = await CreateBatchAsync(con, tx, otherPoolId, now, ("addr-a", 1m));
            await CreateAttemptAsync(con, tx, otherBatch, 1, "execution-other-pool", now, otherBatch.Intents[0].Id);

            var limited = await repo.GetPreparedAttemptsForExecutionAsync(con, tx, poolId, 1, Ct);
            Assert.Equal(new[] { preparedFirst.Id }, limited.Select(x => x.Id).ToArray());

            var all = await repo.GetPreparedAttemptsForExecutionAsync(con, tx, poolId, 10, Ct);
            Assert.Equal(new[] { preparedFirst.Id, preparedSecond.Id }, all.Select(x => x.Id).ToArray());
            Assert.All(all, attempt =>
            {
                Assert.Equal(poolId, attempt.PoolId);
                Assert.Equal(PayoutSendAttemptStates.Prepared, attempt.State);
            });

            await SetBatchStateAsync(con, tx, batch.Id, PayoutBatchStates.Sending);

            var fromSendingBatch = await repo.GetPreparedAttemptsForExecutionAsync(con, tx, poolId, 10, Ct);
            Assert.Equal(new[] { preparedFirst.Id, preparedSecond.Id }, fromSendingBatch.Select(x => x.Id).ToArray());
        });
    }

    [PostgresIntegrationFact]
    public async Task GetPreparedAttemptsForExecutionAsync_IgnoresPreparedAttemptsFromNonExecutableBatches()
    {
        await AssertPreparedAttemptsIgnoredForBatchStateAsync(PayoutBatchStates.Submitted, "execution_submitted");
        await AssertPreparedAttemptsIgnoredForBatchStateAsync(PayoutBatchStates.Cancelled, "execution_cancelled");
        await AssertPreparedAttemptsIgnoredForBatchStateAsync(PayoutBatchStates.Failed, "execution_failed");
        await AssertPreparedAttemptsIgnoredForBatchStateAsync(PayoutBatchStates.AmbiguousRequiresReview, "execution_ambiguous");
        await AssertPreparedAttemptsIgnoredForBatchStateAsync(PayoutBatchStates.Settled, "execution_settled");
    }

    [PostgresIntegrationFact]
    public Task GetPreparedAttemptsForExecutionAsync_RequiresTransactionAndValidArguments()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetPreparedAttemptsForExecutionAsync(null, tx, "pool", 1, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetPreparedAttemptsForExecutionAsync(con, null, "pool", 1, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.GetPreparedAttemptsForExecutionAsync(con, tx, " ", 1, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.GetPreparedAttemptsForExecutionAsync(con, tx, "pool", 0, Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task GetSendAttemptForExecutionAsync_LoadsAttemptByIdAndPool()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("execution_attempt");
            var batch = await CreateBatchAsync(con, tx, poolId, UtcNow(), ("addr-a", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "execution-attempt", UtcNow(), batch.Intents[0].Id);

            var loaded = await repo.GetSendAttemptForExecutionAsync(con, tx, attempt.Id, poolId, Ct);
            var wrongPool = await repo.GetSendAttemptForExecutionAsync(con, tx, attempt.Id, NewPoolId("wrong_pool"), Ct);

            Assert.NotNull(loaded);
            Assert.Equal(attempt.Id, loaded.Id);
            Assert.Equal(batch.Id, loaded.BatchId);
            Assert.Equal(PayoutSendAttemptStates.Prepared, loaded.State);
            Assert.Null(wrongPool);

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetSendAttemptForExecutionAsync(con, null, attempt.Id, poolId, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.GetSendAttemptForExecutionAsync(con, tx, attempt.Id, " ", Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.GetSendAttemptForExecutionAsync(con, tx, 0, poolId, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetAttemptExecutionContextAsync(con, null, attempt.Id, poolId, Ct));
        });
    }

    [PostgresIntegrationFact]
    public Task GetAttemptExecutionContextAsync_ReturnsBatchAttemptAndMappedIntentsOnly()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("execution_context");
            var batch = await CreateBatchAsync(con, tx, poolId, UtcNow(),
                ("addr-b", 2m), ("addr-a", 1m), ("addr-c", 3m));
            var first = await CreateAttemptAsync(con, tx, batch, 1, "execution-context-first", UtcNow(),
                batch.Intents[0].Id, batch.Intents[1].Id);
            var second = await CreateAttemptAsync(con, tx, batch, 2, "execution-context-second", UtcNow(), batch.Intents[2].Id);

            var context = await repo.GetAttemptExecutionContextAsync(con, tx, first.Id, poolId, Ct);
            var wrongPool = await repo.GetAttemptExecutionContextAsync(con, tx, first.Id, NewPoolId("context_wrong_pool"), Ct);

            Assert.NotNull(context);
            Assert.Equal(batch.Id, context.Batch.Id);
            Assert.Equal(first.Id, context.Attempt.Id);
            Assert.Equal(new[] { "addr-a", "addr-b" }, context.Intents.Select(x => x.Address).ToArray());
            Assert.DoesNotContain(context.Intents, x => x.AttemptId == second.Id);
            Assert.All(context.Intents, intent =>
            {
                Assert.Equal(first.Id, intent.AttemptId);
                Assert.Equal(poolId, intent.PoolId);
                Assert.Equal(Coin, intent.Coin);
                Assert.Equal(PayoutIntentStates.Reserved, intent.IntentState);
                Assert.Equal(PayoutAttemptIntentStates.Active, intent.AttemptIntentState);
            });
            Assert.Null(wrongPool);
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutionContextReadsDoNotMutateAccountingOrBalances()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("execution_no_mutation");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr-balance", 10m, now);
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m), ("addr-b", 2m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "execution-no-mutation", now,
                batch.Intents.Select(x => x.Id).ToArray());

            var balanceBefore = await SumBalancesAsync(con, tx, poolId);
            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);

            await repo.GetPreparedAttemptsForExecutionAsync(con, tx, poolId, 10, Ct);
            await repo.GetSendAttemptForExecutionAsync(con, tx, attempt.Id, poolId, Ct);
            await repo.GetAttemptExecutionContextAsync(con, tx, attempt.Id, poolId, Ct);

            Assert.Equal(balanceBefore, await SumBalancesAsync(con, tx, poolId));
            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, tx, attempt.Id));
            Assert.Equal(batch.Intents.Length, await CountAttemptMappingsAsync(con, tx, attempt.Id, PayoutAttemptIntentStates.Active));
            Assert.Equal(batch.Intents.Length, await CountBatchIntentsAsync(con, tx, batch.Id, PayoutIntentStates.Reserved));
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

    private static Task SetAttemptStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId, string state)
    {
        return con.ExecuteAsync("UPDATE payout_send_attempts SET state = @state WHERE id = @attemptid",
            new { attemptid = attemptId, state }, tx);
    }

    private static Task SetBatchStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId, string state)
    {
        return con.ExecuteAsync("UPDATE payout_batches SET state = @state WHERE id = @batchid",
            new { batchid = batchId, state }, tx);
    }

    private Task AssertPreparedAttemptsIgnoredForBatchStateAsync(string batchState, string suffix)
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId(suffix);
            var batch = await CreateBatchAsync(con, tx, poolId, UtcNow(), ("addr-a", 1m));
            await CreateAttemptAsync(con, tx, batch, 1, $"prepared-{batchState}", UtcNow(), batch.Intents[0].Id);
            await SetBatchStateAsync(con, tx, batch.Id, batchState);

            var attempts = await repo.GetPreparedAttemptsForExecutionAsync(con, tx, poolId, 10, Ct);

            Assert.Empty(attempts);
        });
    }

    private static Task<string> GetAttemptStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_send_attempts WHERE id = @attemptid",
            new { attemptid = attemptId }, tx);
    }

    private static Task<int> CountAttemptMappingsAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId, string state)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_attempt_intents
            WHERE attemptid = @attemptid AND state = @state",
            new { attemptid = attemptId, state }, tx);
    }

    private static Task<int> CountBatchIntentsAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId, string state)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_intents
            WHERE batchid = @batchid AND state = @state",
            new { batchid = batchId, state }, tx);
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

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, IDbTransaction tx, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid", new { poolid = poolId }, tx);
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }
}
