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

public class PayoutReservationRepositoryTests : PostgresIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutReservationRepository repo = new();

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_SelectsBalancesOverPoolMinimum()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_minimum");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr-low", 4.99m, now);
            await InsertBalanceAsync(con, tx, poolId, "addr-high", 5.00m, now.AddSeconds(1));

            var candidates = await repo.GetEligibleCandidatesAsync(con, tx, poolId, 5m, 100, Ct);

            var candidate = Assert.Single(candidates);
            Assert.Equal("addr-high", candidate.Address);
            Assert.Equal(5.00m, candidate.BalanceSnapshotAmount);
            Assert.Equal(5m, candidate.PaymentThreshold);
            Assert.Equal(0m, candidate.ReservedAmount);
            Assert.Equal(0m, candidate.AmbiguousAmount);
            Assert.Equal(5.00m, candidate.AvailableAmount);
        });
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_MinimumPaymentZeroAllowsPositiveBalances()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_zero_minimum");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr-zero", 0m, now);
            await InsertBalanceAsync(con, tx, poolId, "addr-positive", 0.00000001m, now.AddSeconds(1));

            var candidates = await repo.GetEligibleCandidatesAsync(con, tx, poolId, 0m, 100, Ct);

            var candidate = Assert.Single(candidates);
            Assert.Equal("addr-positive", candidate.Address);
            Assert.Equal(0.00000001m, candidate.AvailableAmount);
        });
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_MinerThresholdOverridesPoolMinimum()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_miner_threshold");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr-default", 9m, now);
            await InsertBalanceAsync(con, tx, poolId, "addr-lower-threshold", 5m, now.AddSeconds(1));
            await InsertBalanceAsync(con, tx, poolId, "addr-higher-threshold", 15m, now.AddSeconds(2));
            await InsertMinerSettingsAsync(con, tx, poolId, "addr-lower-threshold", 5m, now);
            await InsertMinerSettingsAsync(con, tx, poolId, "addr-higher-threshold", 20m, now);

            var candidates = await repo.GetEligibleCandidatesAsync(con, tx, poolId, 10m, 100, Ct);

            var candidate = Assert.Single(candidates);
            Assert.Equal("addr-lower-threshold", candidate.Address);
            Assert.Equal(5m, candidate.PaymentThreshold);
        });
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_ExcludesActiveReservedIntent()
    {
        return AssertActiveIntentExclusionAsync(PayoutBatchStates.Reserved, PayoutIntentStates.Reserved, "active_reserved");
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_ExcludesActiveSendingIntent()
    {
        return AssertActiveIntentExclusionAsync(PayoutBatchStates.Sending, PayoutIntentStates.Sending, "active_sending");
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_ExcludesActiveSubmittedIntent()
    {
        return AssertActiveIntentExclusionAsync(PayoutBatchStates.Submitted, PayoutIntentStates.Submitted, "active_submitted");
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_ExcludesActiveAmbiguousIntent()
    {
        return AssertActiveIntentExclusionAsync(PayoutBatchStates.AmbiguousRequiresReview,
            PayoutIntentStates.AmbiguousRequiresReview, "active_ambiguous");
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_IgnoresCancelledAndFailedIntents()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_inactive_intents");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr-cancelled", 10m, now);
            await InsertBalanceAsync(con, tx, poolId, "addr-failed", 11m, now.AddSeconds(1));
            await InsertPayoutIntentAsync(con, tx, poolId, "addr-cancelled", PayoutBatchStates.Cancelled,
                PayoutIntentStates.Cancelled, 1m, now);
            await InsertPayoutIntentAsync(con, tx, poolId, "addr-failed", PayoutBatchStates.Failed,
                PayoutIntentStates.Failed, 1m, now);

            var candidates = await repo.GetEligibleCandidatesAsync(con, tx, poolId, 1m, 100, Ct);

            Assert.Equal(new[] { "addr-cancelled", "addr-failed" }, candidates.Select(x => x.Address).ToArray());
        });
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_ReturnsNoCandidatesWhenNoneEligible()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_none");
            await InsertBalanceAsync(con, tx, poolId, "addr-low", 1m, UtcNow());

            var candidates = await repo.GetEligibleCandidatesAsync(con, tx, poolId, 10m, 100, Ct);

            Assert.Empty(candidates);
        });
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_RespectsMaxCandidatesAndOrder()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_limit");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr-2", 10m, now.AddSeconds(2));
            await InsertBalanceAsync(con, tx, poolId, "addr-1", 10m, now.AddSeconds(1));
            await InsertBalanceAsync(con, tx, poolId, "addr-3", 10m, now.AddSeconds(3));

            var candidates = await repo.GetEligibleCandidatesAsync(con, tx, poolId, 1m, 2, Ct);

            Assert.Equal(new[] { "addr-1", "addr-2" }, candidates.Select(x => x.Address).ToArray());
        });
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_DoesNotMutateBalancesPaymentsOrBalanceChanges()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_no_mutation");
            await InsertBalanceAsync(con, tx, poolId, "addr", 10m, UtcNow());

            var balanceBefore = await SumBalancesAsync(con, tx, poolId);
            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);
            var batchesBefore = await CountPoolRowsAsync(con, tx, "payout_batches", poolId);
            var intentsBefore = await CountPoolRowsAsync(con, tx, "payout_intents", poolId);

            var candidates = await repo.GetEligibleCandidatesAsync(con, tx, poolId, 1m, 100, Ct);

            Assert.Single(candidates);
            Assert.Equal(balanceBefore, await SumBalancesAsync(con, tx, poolId));
            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(batchesBefore, await CountPoolRowsAsync(con, tx, "payout_batches", poolId));
            Assert.Equal(intentsBefore, await CountPoolRowsAsync(con, tx, "payout_intents", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task GetEligibleCandidatesAsync_RequiresValidArgumentsAndTransaction()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetEligibleCandidatesAsync(null, tx, "pool", 1m, 1, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repo.GetEligibleCandidatesAsync(con, null, "pool", 1m, 1, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.GetEligibleCandidatesAsync(con, tx, " ", 1m, 1, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.GetEligibleCandidatesAsync(con, tx, "pool", -1m, 1, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repo.GetEligibleCandidatesAsync(con, tx, "pool", 1m, 0, Ct));
        });
    }

    private Task AssertActiveIntentExclusionAsync(string batchState, string intentState, string suffix)
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId($"reservation_{suffix}");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr-blocked", 10m, now);
            await InsertBalanceAsync(con, tx, poolId, "addr-eligible", 11m, now.AddSeconds(1));
            await InsertPayoutIntentAsync(con, tx, poolId, "addr-blocked", batchState, intentState, 1m, now);

            var candidates = await repo.GetEligibleCandidatesAsync(con, tx, poolId, 1m, 100, Ct);

            var candidate = Assert.Single(candidates);
            Assert.Equal("addr-eligible", candidate.Address);
        });
    }

    private static async Task<long> InsertPayoutIntentAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId,
        string address, string batchState, string intentState, decimal amount, DateTime created)
    {
        const string insertBatchQuery = @"INSERT INTO payout_batches(poolid, coin, coinfamily, handler, state, sendshape,
                recipientsethash, minimumamount, reservedamountsnapshot, intentcountsnapshot, created, updated)
            VALUES(@poolid, @coin, @coinfamily, @handler, @state, @sendshape, @recipientsethash, @minimumamount,
                @reservedamountsnapshot, @intentcountsnapshot, @created, @created)
            RETURNING id";

        var batchId = await con.QuerySingleAsync<long>(insertBatchQuery, new
        {
            poolid = poolId,
            coin = Coin,
            coinfamily = CoinFamily,
            handler = Handler,
            state = batchState,
            sendshape = PayoutSendShapes.PerAddress,
            recipientsethash = $"recipient-set-{Guid.NewGuid():N}",
            minimumamount = 0m,
            reservedamountsnapshot = amount,
            intentcountsnapshot = 1,
            created
        }, tx);

        const string insertIntentQuery = @"INSERT INTO payout_intents(batchid, poolid, coin, address, state, amount,
                balancesnapshotamount, balancesnapshotupdated, paymentthreshold, created, updated)
            VALUES(@batchid, @poolid, @coin, @address, @state, @amount, @amount, @created, 0, @created, @created)
            RETURNING id";

        return await con.QuerySingleAsync<long>(insertIntentQuery, new
        {
            batchid = batchId,
            poolid = poolId,
            coin = Coin,
            address,
            state = intentState,
            amount,
            created
        }, tx);
    }

    private static Task InsertBalanceAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId, string address,
        decimal amount, DateTime created)
    {
        return con.ExecuteAsync(@"INSERT INTO balances(poolid, address, amount, created, updated)
            VALUES(@poolid, @address, @amount, @created, @created)",
            new { poolid = poolId, address, amount, created }, tx);
    }

    private static Task InsertMinerSettingsAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId, string address,
        decimal paymentThreshold, DateTime created)
    {
        return con.ExecuteAsync(@"INSERT INTO miner_settings(poolid, address, paymentthreshold, created, updated)
            VALUES(@poolid, @address, @paymentthreshold, @created, @created)",
            new { poolid = poolId, address, paymentthreshold = paymentThreshold, created }, tx);
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
