using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HashStormCore.Payments;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Postgres;
using HashStormCore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace HashStormCore.Tests.Persistence.Postgres;

public class DbPayoutStaleSendReconciliationRunnerTests : PostgresCommittedIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository repo = new();

    [PostgresIntegrationFact]
    public async Task ReconcileStaleSendingAsync_ValidatesRequest()
    {
        var runner = NewRunner();
        var now = UtcNow();
        var request = NewRequest("pool", now.AddMinutes(-1), now);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ReconcileStaleSendingAsync(request with { PoolId = " " }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.ReconcileStaleSendingAsync(request with { Limit = 0 }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ReconcileStaleSendingAsync(request with { OlderThan = default }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ReconcileStaleSendingAsync(request with { Updated = default }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ReconcileStaleSendingAsync(request with { Updated = now.AddMinutes(-2) }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ReconcileStaleSendingAsync(request with { ErrorCode = " " }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ReconcileStaleSendingAsync(request with { ErrorMessage = " " }, Ct));
    }

    [PostgresIntegrationFact]
    public Task ReconcileStaleSendingAsync_PropagatesCancellation()
    {
        var runner = NewRunner();
        var now = UtcNow();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        return Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.ReconcileStaleSendingAsync(NewRequest("pool", now.AddMinutes(-1), now), cts.Token));
    }

    [PostgresIntegrationFact]
    public Task ReconcileStaleSendingAsync_MarksStaleSendingBatchAmbiguous()
    {
        var poolId = NewCommittedPoolId("stale_runner_marks");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var old = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var data = await CreateAttemptAsync(con, poolId, old, ("addr-a", 1m));
            await MarkSendingAsync(con, data.Attempt.Id, poolId, old);

            var result = await NewRunner().ReconcileStaleSendingAsync(
                NewRequest(poolId, old.AddMinutes(1), old.AddMinutes(2)), Ct);

            Assert.Equal(1, result.CandidateBatchCount);
            Assert.Equal(new[] { data.Batch.Id }, result.MarkedBatchIds.ToArray());
            Assert.Empty(result.SkippedBatchIds);
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, data.Batch.Id));
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview,
                await GetAttemptStateAsync(con, data.Attempt.Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview,
                await GetIntentStateAsync(con, data.Batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileStaleSendingAsync_DoesNotMarkNonStaleSendingBatch()
    {
        var poolId = NewCommittedPoolId("stale_runner_fresh");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var data = await CreateAttemptAsync(con, poolId, now, ("addr-a", 1m));
            await MarkSendingAsync(con, data.Attempt.Id, poolId, now);

            var result = await NewRunner().ReconcileStaleSendingAsync(
                NewRequest(poolId, now.AddMinutes(-1), now.AddMinutes(1)), Ct);

            Assert.Equal(0, result.CandidateBatchCount);
            Assert.Empty(result.MarkedBatchIds);
            Assert.Equal(PayoutBatchStates.Sending, await GetBatchStateAsync(con, data.Batch.Id));
            Assert.Equal(PayoutSendAttemptStates.Sending, await GetAttemptStateAsync(con, data.Attempt.Id));
            Assert.Equal(PayoutIntentStates.Sending, await GetIntentStateAsync(con, data.Batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ReconcileStaleSendingAsync_DoesNotMarkPreparedOrReservedBatch()
    {
        var poolId = NewCommittedPoolId("stale_runner_prepared");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var old = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var data = await CreateAttemptAsync(con, poolId, old, ("addr-a", 1m));

            var result = await NewRunner().ReconcileStaleSendingAsync(
                NewRequest(poolId, old.AddMinutes(1), old.AddMinutes(2)), Ct);

            Assert.Equal(0, result.CandidateBatchCount);
            Assert.Empty(result.MarkedBatchIds);
            Assert.Equal(PayoutBatchStates.Reserved, await GetBatchStateAsync(con, data.Batch.Id));
            Assert.Equal(PayoutSendAttemptStates.Prepared, await GetAttemptStateAsync(con, data.Attempt.Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, data.Batch.Intents[0].Id));
        });
    }

    private DbPayoutStaleSendReconciliationRunner NewRunner()
    {
        return new DbPayoutStaleSendReconciliationRunner(new PgConnectionFactory(GetConnectionString()),
            new PayoutStaleSendReconciliationService(repo));
    }

    private static PayoutStaleSendReconciliationRunnerRequest NewRequest(string poolId, DateTime olderThan,
        DateTime updated, int limit = 10)
    {
        return new PayoutStaleSendReconciliationRunnerRequest
        {
            PoolId = poolId,
            OlderThan = olderThan,
            Updated = updated,
            Limit = limit,
            ErrorCode = "stale_send_reconciliation",
            ErrorMessage = "Payout send attempt stayed in sending past stale threshold"
        };
    }

    private async Task<TestPayoutData> CreateAttemptAsync(NpgsqlConnection con, string poolId, DateTime created,
        params (string address, decimal amount)[] intents)
    {
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            var batch = await CreateBatchAsync(con, tx, poolId, created, intents);
            var attempt = await CreateSendAttemptAsync(con, tx, batch, created, batch.Intents.Select(x => x.Id).ToArray());
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
            SendShape = PayoutSendShapes.PerAddress,
            RecipientSetHash = $"stale-runner-recipient-set-{Guid.NewGuid():N}",
            MinimumAmount = 0m,
            ReservedAmountSnapshot = intents.Sum(x => x.amount),
            IntentCountSnapshot = intents.Length,
            Created = created
        }, intentRequests, Ct);
    }

    private Task<PayoutSendAttempt> CreateSendAttemptAsync(NpgsqlConnection con, NpgsqlTransaction tx,
        PayoutBatch batch, DateTime created, params long[] intentIds)
    {
        var selectedIntents = batch.Intents.Where(x => intentIds.Contains(x.Id)).ToArray();

        return repo.CreateSendAttemptAsync(con, tx, new CreatePayoutSendAttemptRequest
        {
            BatchId = batch.Id,
            PoolId = batch.PoolId,
            Coin = batch.Coin,
            AttemptNo = 1,
            Method = Method,
            RequestHash = $"stale-runner-request-{Guid.NewGuid():N}",
            RequestSummary = $"test:recipients={selectedIntents.Length}",
            RecipientCount = selectedIntents.Length,
            AmountSnapshot = selectedIntents.Sum(x => x.Amount),
            Created = created
        }, intentIds, Ct);
    }

    private async Task MarkSendingAsync(NpgsqlConnection con, long attemptId, string poolId, DateTime updated)
    {
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            await repo.MarkAttemptSendingAsync(con, tx, attemptId, poolId, updated, Ct);
            await con.ExecuteAsync("UPDATE payout_send_attempts SET updated = @updated WHERE id = @attemptid",
                new { attemptid = attemptId, updated }, tx);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
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

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private sealed record TestPayoutData(PayoutBatch Batch, PayoutSendAttempt Attempt);
}
