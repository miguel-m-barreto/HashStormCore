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

public class PayoutReservationServiceTests : PostgresIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository payoutIntentRepo = new();
    private readonly PayoutReservationRepository payoutReservationRepo = new();
    private readonly PayoutReservationService service;

    public PayoutReservationServiceTests()
    {
        service = new PayoutReservationService(payoutIntentRepo, payoutReservationRepo);
    }

    [PostgresIntegrationFact]
    public Task CreateReservationAsync_ActiveBatchReturnsActiveBatchExists()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_service_active");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr-eligible", 10m, now);
            var activeBatchId = await InsertPayoutBatchWithIntentAsync(con, tx, poolId, "addr-active",
                PayoutBatchStates.Reserved, PayoutIntentStates.Reserved, 1m, now);

            var batchCountBefore = await CountPoolRowsAsync(con, tx, "payout_batches", poolId);
            var intentCountBefore = await CountPoolRowsAsync(con, tx, "payout_intents", poolId);

            var result = await service.CreateReservationAsync(con, tx, NewRequest(poolId, now), Ct);

            Assert.Equal(PayoutReservationStatus.ActiveBatchExists, result.Status);
            Assert.NotNull(result.Batch);
            Assert.Equal(activeBatchId, result.Batch.Id);
            Assert.Equal(batchCountBefore, await CountPoolRowsAsync(con, tx, "payout_batches", poolId));
            Assert.Equal(intentCountBefore, await CountPoolRowsAsync(con, tx, "payout_intents", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateReservationAsync_NoEligibleBalancesReturnsNoEligibleBalances()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_service_none");
            await InsertBalanceAsync(con, tx, poolId, "addr-low", 1m, UtcNow());

            var result = await service.CreateReservationAsync(con, tx, NewRequest(poolId, UtcNow(), minimumPayment: 10m), Ct);

            Assert.Equal(PayoutReservationStatus.NoEligibleBalances, result.Status);
            Assert.Null(result.Batch);
            Assert.Empty(result.Intents);
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payout_batches", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payout_intents", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateReservationAsync_CreatesReservedBatchAndIntentsFromCandidates()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_service_create");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr-two", 20m, now.AddSeconds(2));
            await InsertBalanceAsync(con, tx, poolId, "addr-one", 10m, now.AddSeconds(1));

            var result = await service.CreateReservationAsync(con, tx, NewRequest(poolId, now, minimumPayment: 5m), Ct);

            Assert.Equal(PayoutReservationStatus.Created, result.Status);
            Assert.NotNull(result.Batch);
            Assert.Equal(PayoutBatchStates.Reserved, result.Batch.State);
            Assert.Equal(poolId, result.Batch.PoolId);
            Assert.Equal(Coin, result.Batch.Coin);
            Assert.Equal(CoinFamily, result.Batch.CoinFamily);
            Assert.Equal(Handler, result.Batch.Handler);
            Assert.Equal(PayoutSendShapes.PerAddress, result.Batch.SendShape);
            Assert.Equal(30m, result.Batch.ReservedAmountSnapshot);
            Assert.Equal(2, result.Batch.IntentCountSnapshot);
            Assert.Equal(2, result.Intents.Count);
            Assert.All(result.Intents, intent => Assert.Equal(PayoutIntentStates.Reserved, intent.State));

            Assert.Equal(1, await CountPoolRowsAsync(con, tx, "payout_batches", poolId));
            Assert.Equal(2, await CountPoolRowsAsync(con, tx, "payout_intents", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payout_send_attempts", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateReservationAsync_CarriesCandidateSnapshotsIntoIntents()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_service_snapshots");
            var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var balanceUpdated = new DateTime(2026, 1, 2, 3, 1, 5, DateTimeKind.Utc);
            await InsertBalanceAsync(con, tx, poolId, "addr-threshold", 12m, balanceUpdated);
            await InsertMinerSettingsAsync(con, tx, poolId, "addr-threshold", 7m, now);

            var result = await service.CreateReservationAsync(con, tx, NewRequest(poolId, now, minimumPayment: 5m), Ct);

            var intent = Assert.Single(result.Intents);
            Assert.Equal("addr-threshold", intent.Address);
            Assert.Equal(12m, intent.Amount);
            Assert.Equal(12m, intent.BalanceSnapshotAmount);
            Assert.Equal(balanceUpdated, intent.BalanceSnapshotUpdated);
            Assert.Equal(7m, intent.PaymentThreshold);
            Assert.Equal(now, intent.Created);
            Assert.Equal(now, intent.Updated);
        });
    }

    [PostgresIntegrationFact]
    public Task CreateReservationAsync_RewardRecipientNullThresholdInheritsPoolMinimum()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_service_reward_inherit");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "reward-low", 4m, now);
            await InsertBalanceAsync(con, tx, poolId, "reward-high", 5m, now.AddSeconds(1));

            var result = await service.CreateReservationAsync(con, tx,
                NewRequest(poolId, now, minimumPayment: 5m) with
                {
                    RewardRecipientThresholds = RewardThreshold("reward-low", null)
                        .Concat(RewardThreshold("reward-high", null))
                        .ToArray()
                }, Ct);

            var intent = Assert.Single(result.Intents);
            Assert.Equal("reward-high", intent.Address);
            Assert.Equal(5m, intent.PaymentThreshold);
        });
    }

    [PostgresIntegrationFact]
    public Task CreateReservationAsync_RewardRecipientZeroThresholdCreatesReservation()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_service_reward_zero");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "reward-zero", 0.00000001m, now);

            var result = await service.CreateReservationAsync(con, tx,
                NewRequest(poolId, now, minimumPayment: 10m) with
                {
                    RewardRecipientThresholds = RewardThreshold("reward-zero", 0m)
                }, Ct);

            var intent = Assert.Single(result.Intents);
            Assert.Equal(PayoutReservationStatus.Created, result.Status);
            Assert.Equal("reward-zero", intent.Address);
            Assert.Equal(0m, intent.PaymentThreshold);
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
        });
    }

    [PostgresIntegrationFact]
    public async Task CreateReservationAsync_RecipientSetHashIsDeterministicIndependentOfInsertionOrder()
    {
        var poolId = NewPoolId("reservation_hash");
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var balanceTime = new DateTime(2026, 1, 2, 2, 4, 5, DateTimeKind.Utc);

        async Task<string> CreateHashAsync(bool reverseInsertOrder)
        {
            string hash = null;

            await WithRollbackAsync(async (con, tx) =>
            {
                if(reverseInsertOrder)
                {
                    await InsertBalanceAsync(con, tx, poolId, "addr-b", 2m, balanceTime);
                    await InsertBalanceAsync(con, tx, poolId, "addr-a", 1m, balanceTime);
                }
                else
                {
                    await InsertBalanceAsync(con, tx, poolId, "addr-a", 1m, balanceTime);
                    await InsertBalanceAsync(con, tx, poolId, "addr-b", 2m, balanceTime);
                }

                var result = await service.CreateReservationAsync(con, tx, NewRequest(poolId, created), Ct);

                Assert.Equal(new[] { "addr-a", "addr-b" }, result.Intents.Select(x => x.Address).ToArray());
                hash = result.Batch.RecipientSetHash;
            });

            return hash;
        }

        var firstHash = await CreateHashAsync(reverseInsertOrder: true);
        var secondHash = await CreateHashAsync(reverseInsertOrder: false);

        Assert.False(string.IsNullOrWhiteSpace(firstHash));
        Assert.Equal(firstHash, secondHash);
    }

    [PostgresIntegrationFact]
    public Task CreateReservationAsync_DoesNotMutateSettledAccountingOrBalances()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("reservation_service_no_mutation");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr", 10m, now);

            var balanceBefore = await SumBalancesAsync(con, tx, poolId);
            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);

            var result = await service.CreateReservationAsync(con, tx, NewRequest(poolId, now), Ct);

            Assert.Equal(PayoutReservationStatus.Created, result.Status);
            Assert.Equal(balanceBefore, await SumBalancesAsync(con, tx, poolId));
            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payout_send_attempts", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateReservationAsync_RejectsInvalidRequestsAndRequiresTransaction()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var valid = NewRequest(NewPoolId("reservation_service_invalid"), UtcNow());

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.CreateReservationAsync(null, tx, valid, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.CreateReservationAsync(con, null, valid, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.CreateReservationAsync(con, tx, null, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateReservationAsync(con, tx, valid with { PoolId = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateReservationAsync(con, tx, valid with { Coin = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateReservationAsync(con, tx, valid with { CoinFamily = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateReservationAsync(con, tx, valid with { Handler = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateReservationAsync(con, tx, valid with { SendShape = "unknown" }, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.CreateReservationAsync(con, tx, valid with { MinimumPayment = -1m }, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.CreateReservationAsync(con, tx, valid with { MaxCandidates = 0 }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateReservationAsync(con, tx, valid with
                {
                    RewardRecipientThresholds = RewardThreshold("reward", 0m).Concat(RewardThreshold("reward", 1m)).ToArray()
                }, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.CreateReservationAsync(con, tx, valid with
                {
                    RewardRecipientThresholds = RewardThreshold("reward", -1m)
                }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateReservationAsync(con, tx, valid with
                {
                    RewardRecipientThresholds = RewardThreshold(" ", 0m)
                }, Ct));
        });
    }

    private static CreatePayoutReservationRequest NewRequest(string poolId, DateTime created,
        decimal minimumPayment = 1m, int maxCandidates = 100)
    {
        return new CreatePayoutReservationRequest
        {
            PoolId = poolId,
            Coin = Coin,
            CoinFamily = CoinFamily,
            Handler = Handler,
            SendShape = PayoutSendShapes.PerAddress,
            MinimumPayment = minimumPayment,
            MaxCandidates = maxCandidates,
            Created = created
        };
    }

    private static async Task<long> InsertPayoutBatchWithIntentAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId,
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
            VALUES(@batchid, @poolid, @coin, @address, @state, @amount, @amount, @created, 0, @created, @created)";

        await con.ExecuteAsync(insertIntentQuery, new
        {
            batchid = batchId,
            poolid = poolId,
            coin = Coin,
            address,
            state = intentState,
            amount,
            created
        }, tx);

        return batchId;
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

    private static PayoutRewardRecipientThreshold[] RewardThreshold(string address, decimal? minimumPayment)
    {
        return new[]
        {
            new PayoutRewardRecipientThreshold
            {
                Address = address,
                MinimumPayment = minimumPayment
            }
        };
    }
}
