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

public class PayoutStaleSendReconciliationServiceTests : PostgresIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository repo = new();
    private readonly PayoutStaleSendReconciliationService service;

    public PayoutStaleSendReconciliationServiceTests()
    {
        service = new PayoutStaleSendReconciliationService(repo);
    }

    [PostgresIntegrationFact]
    public Task MarkStaleSendingBatchesAmbiguousAsync_MarksSingleStaleBatchAmbiguous()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("stale_service_single");
            var old = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var batch = await CreateBatchAsync(con, tx, poolId, old, ("addr-a", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "stale-service-single", old, batch.Intents[0].Id);
            await InsertBalanceAsync(con, tx, poolId, "addr-a", 10m, old);

            await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, old, Ct);
            await SetAttemptUpdatedAsync(con, tx, attempt.Id, old);

            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);
            var balanceAmountBefore = await SumBalancesAsync(con, tx, poolId);

            var result = await service.MarkStaleSendingBatchesAmbiguousAsync(con, tx,
                NewRequest(poolId, old.AddMinutes(1), old.AddMinutes(2)), Ct);

            Assert.Equal(1, result.CandidateBatchCount);
            Assert.Equal(1, result.MarkedBatchCount);
            Assert.Equal(new[] { batch.Id }, result.MarkedBatchIds.ToArray());
            Assert.Empty(result.SkippedBatchIds);
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, attempt.Id));
            Assert.Equal(PayoutAttemptIntentStates.AmbiguousRequiresReview,
                await GetAttemptIntentStateAsync(con, tx, attempt.Id, batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(balanceAmountBefore, await SumBalancesAsync(con, tx, poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkStaleSendingBatchesAmbiguousAsync_IgnoresNonStaleSendingBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("stale_service_fresh");
            var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "stale-service-fresh", now, batch.Intents[0].Id);

            await repo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, now, Ct);
            await SetAttemptUpdatedAsync(con, tx, attempt.Id, now);

            var result = await service.MarkStaleSendingBatchesAmbiguousAsync(con, tx,
                NewRequest(poolId, now.AddMinutes(-1), now.AddMinutes(1)), Ct);

            Assert.Equal(0, result.CandidateBatchCount);
            Assert.Equal(0, result.MarkedBatchCount);
            Assert.Equal(PayoutBatchStates.Sending, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.Sending, await GetAttemptStateAsync(con, tx, attempt.Id));
            Assert.Equal(PayoutIntentStates.Sending, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkStaleSendingBatchesAmbiguousAsync_QuarantinesPreparedSiblingInMultiAttemptBatch()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("stale_service_multi");
            var old = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var batch = await CreateBatchAsync(con, tx, poolId, old, ("addr-a", 1m), ("addr-b", 2m));
            var stale = await CreateAttemptAsync(con, tx, batch, 1, "stale-service-multi-a", old, batch.Intents[0].Id);
            var prepared = await CreateAttemptAsync(con, tx, batch, 2, "stale-service-multi-b", old.AddSeconds(1), batch.Intents[1].Id);

            await repo.MarkAttemptSendingAsync(con, tx, stale.Id, poolId, old, Ct);
            await SetAttemptUpdatedAsync(con, tx, stale.Id, old);

            var result = await service.MarkStaleSendingBatchesAmbiguousAsync(con, tx,
                NewRequest(poolId, old.AddMinutes(1), old.AddMinutes(2)), Ct);

            Assert.Equal(1, result.MarkedBatchCount);
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, stale.Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, prepared.Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, batch.Intents[1].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task MarkStaleSendingBatchesAmbiguousAsync_RequiresTransactionAndValidRequest()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var request = NewRequest("pool", UtcNow(), UtcNow());

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.MarkStaleSendingBatchesAmbiguousAsync(null, tx, request, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.MarkStaleSendingBatchesAmbiguousAsync(con, null, request, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.MarkStaleSendingBatchesAmbiguousAsync(con, tx, null, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.MarkStaleSendingBatchesAmbiguousAsync(con, tx, request with { PoolId = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.MarkStaleSendingBatchesAmbiguousAsync(con, tx, request with { ErrorCode = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.MarkStaleSendingBatchesAmbiguousAsync(con, tx, request with { ErrorMessage = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.MarkStaleSendingBatchesAmbiguousAsync(con, tx, request with { Limit = 0 }, Ct));
        });
    }

    private static PayoutStaleSendReconciliationRequest NewRequest(string poolId, DateTime olderThan,
        DateTime updated, int limit = 10)
    {
        return new PayoutStaleSendReconciliationRequest
        {
            PoolId = poolId,
            OlderThan = olderThan,
            Updated = updated,
            Limit = limit,
            ErrorCode = "stale_sending_requires_review",
            ErrorMessage = null
        };
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

    private static Task InsertBalanceAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId, string address,
        decimal amount, DateTime created)
    {
        return con.ExecuteAsync(@"INSERT INTO balances(poolid, address, amount, created, updated)
            VALUES(@poolid, @address, @amount, @created, @created)",
            new { poolid = poolId, address, amount, created }, tx);
    }

    private static Task SetAttemptUpdatedAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId, DateTime updated)
    {
        return con.ExecuteAsync("UPDATE payout_send_attempts SET updated = @updated WHERE id = @attemptid",
            new { attemptid = attemptId, updated }, tx);
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

    private static Task<string> GetAttemptIntentStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId, long intentId)
    {
        return con.QuerySingleAsync<string>(
            "SELECT state FROM payout_attempt_intents WHERE attemptid = @attemptid AND intentid = @intentid",
            new { attemptid = attemptId, intentid = intentId }, tx);
    }

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, IDbTransaction tx, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid", new { poolid = poolId }, tx);
    }

    private static Task<decimal> SumBalancesAsync(NpgsqlConnection con, IDbTransaction tx, string poolId)
    {
        return con.QuerySingleAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM balances WHERE poolid = @poolid",
            new { poolid = poolId }, tx);
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }
}
