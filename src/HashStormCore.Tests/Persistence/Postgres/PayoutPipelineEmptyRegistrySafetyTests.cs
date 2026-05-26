using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Postgres;
using HashStormCore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace HashStormCore.Tests.Persistence.Postgres;

public class PayoutPipelineEmptyRegistrySafetyTests : PostgresCommittedIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository payoutIntentRepo = new();
    private readonly PayoutReservationRepository payoutReservationRepo = new();
    private readonly PayoutSettlementRepository payoutSettlementRepo = new();

    [PostgresIntegrationFact]
    public Task EmptySenderAndProviderRegistriesDoNotSendAttachEvidenceSettleOrDebit()
    {
        var poolId = NewCommittedPoolId("empty_registry_pipeline");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var profile = ReadyTxIdProfile();
            await InsertBalanceAsync(con, poolId, "addr-one", 3m, now.AddSeconds(-2));
            await InsertBalanceAsync(con, poolId, "addr-two", 4m, now.AddSeconds(-1));
            var balanceBefore = await SumBalancesAsync(con, poolId);

            var resolver = new DictionaryProfileResolver(profile);
            var connectionFactory = new PgConnectionFactory(GetConnectionString());
            var reservationRunner = new DbPayoutReservationRunner(connectionFactory,
                new PayoutReservationService(payoutIntentRepo, payoutReservationRepo));
            var planningRunner = new DbPayoutPlanningRunner(connectionFactory, payoutIntentRepo,
                new PayoutSendAttemptPlannerService(payoutIntentRepo), resolver);
            var executionRunner = new DbPayoutExecutionRunner(connectionFactory, payoutIntentRepo,
                new PayoutSendExecutorService(connectionFactory, payoutIntentRepo, resolver), resolver,
                new PayoutAttemptSenderRegistry(Array.Empty<PayoutAttemptSenderRegistration>()));
            var staleRunner = new DbPayoutStaleSendReconciliationRunner(connectionFactory,
                new PayoutStaleSendReconciliationService(payoutIntentRepo));
            var operationIdRunner = new DbPayoutOperationIdReconciliationRunner(connectionFactory, payoutIntentRepo,
                resolver,
                new PayoutOperationStatusProviderRegistry(
                    Array.Empty<PayoutOperationStatusProviderRegistration>()),
                new PayoutOperationIdReconciliationService(connectionFactory, payoutIntentRepo, resolver));
            var settlementRunner = new DbPayoutSettlementRunner(connectionFactory, payoutSettlementRepo,
                new PayoutSettlementService(payoutSettlementRepo, resolver));

            var reservation = await reservationRunner.CreateReservationAsync(new CreatePayoutReservationRequest
            {
                PoolId = poolId,
                Coin = Coin,
                CoinFamily = CoinFamily,
                Handler = Handler,
                SendShape = PayoutSendShapes.BatchMultiRecipient,
                MinimumPayment = 1m,
                MaxCandidates = 10,
                Created = now
            }, Ct);

            Assert.Equal(PayoutReservationStatus.Created, reservation.Status);

            var planning = await planningRunner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
            {
                PoolId = poolId,
                MaxBatches = 10,
                Created = now.AddSeconds(1)
            }, Ct);

            Assert.Equal(1, planning.PlannedBatchCount);
            Assert.Equal(1, planning.Results.Single().Attempts.Count);

            var execution = await executionRunner.ExecutePreparedAttemptsAsync(new PayoutExecutionRunnerRequest
            {
                PoolId = poolId,
                MaxAttempts = 10,
                Started = now.AddSeconds(2)
            }, Ct);

            var stale = await staleRunner.ReconcileStaleSendingAsync(new PayoutStaleSendReconciliationRunnerRequest
            {
                PoolId = poolId,
                OlderThan = now.AddMinutes(-30),
                Updated = now.AddSeconds(3),
                Limit = 10,
                ErrorCode = "test_stale_guard",
                ErrorMessage = "test stale guard"
            }, Ct);

            var operationId = await operationIdRunner.ReconcileOperationIdsAsync(
                new PayoutOperationIdReconciliationRunnerRequest
                {
                    PoolId = poolId,
                    Limit = 10,
                    CheckedAt = now.AddSeconds(4)
                }, Ct);

            var settlement = await settlementRunner.SettleAcceptedAttemptsAsync(new PayoutSettlementRunnerRequest
            {
                PoolId = poolId,
                Limit = 10,
                SettledAt = now.AddSeconds(5)
            }, Ct);

            Assert.Equal(1, execution.CandidateAttemptCount);
            Assert.Equal(0, execution.ExecutedAttemptCount);
            Assert.Equal(1, execution.SkippedAttemptCount);
            Assert.Contains("no registered payout sender", Assert.Single(execution.SkippedAttempts).Reason);

            Assert.Equal(0, stale.CandidateBatchCount);
            Assert.Empty(stale.MarkedBatchIds);
            Assert.Equal(0, operationId.CandidateCount);
            Assert.Equal(0, operationId.EvidenceAttachedCount);
            Assert.Equal(0, settlement.CandidateCount);
            Assert.Equal(0, settlement.SettledCount);

            Assert.Equal(1, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.Prepared));
            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.Sending));
            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.Accepted));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.RawHash));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.OperationId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountNegativeBalanceChangesAsync(con, poolId));
            Assert.Equal(balanceBefore, await SumBalancesAsync(con, poolId));
            Assert.Equal(0, await CountIntentsInStateAsync(con, poolId, PayoutIntentStates.Settled));
        });
    }

    private static PayoutProfile ReadyTxIdProfile()
    {
        return new PayoutProfile
        {
            CoinKey = Coin,
            CoinFamily = CoinFamily,
            AdapterId = Handler,
            SendShape = PayoutSendShapes.BatchMultiRecipient,
            SendMethod = Method,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
            ReservationReady = true
        };
    }

    private static Task InsertBalanceAsync(NpgsqlConnection con, string poolId, string address, decimal amount,
        DateTime created)
    {
        return con.ExecuteAsync(@"INSERT INTO balances(poolid, address, amount, created, updated)
            VALUES(@poolid, @address, @amount, @created, @created)",
            new { poolid = poolId, address, amount, created });
    }

    private static Task<decimal> SumBalancesAsync(NpgsqlConnection con, string poolId)
    {
        return con.QuerySingleAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM balances WHERE poolid = @poolid",
            new { poolid = poolId });
    }

    private static Task<int> CountAttemptsInStateAsync(NpgsqlConnection con, string poolId, string state)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_send_attempts
            WHERE poolid = @poolid AND state = @state",
            new { poolid = poolId, state });
    }

    private static Task<int> CountIntentsInStateAsync(NpgsqlConnection con, string poolId, string state)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_intents
            WHERE poolid = @poolid AND state = @state",
            new { poolid = poolId, state });
    }

    private static Task<int> CountConfirmationsAsync(NpgsqlConnection con, string poolId, string kind)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_external_confirmations
            WHERE poolid = @poolid AND kind = @kind",
            new { poolid = poolId, kind });
    }

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid",
            new { poolid = poolId });
    }

    private static Task<int> CountNegativeBalanceChangesAsync(NpgsqlConnection con, string poolId)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM balance_changes
            WHERE poolid = @poolid AND amount < 0",
            new { poolid = poolId });
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private sealed class DictionaryProfileResolver : IPayoutProfileResolver
    {
        public DictionaryProfileResolver(params PayoutProfile[] profiles)
        {
            this.profiles = profiles.ToDictionary(x => x.CoinKey, StringComparer.Ordinal);
        }

        private readonly Dictionary<string, PayoutProfile> profiles;

        public PayoutProfileResolution Resolve(string coinKey)
        {
            return profiles.TryGetValue(coinKey, out var profile)
                ? PayoutProfileResolution.Resolved(profile)
                : PayoutProfileResolution.Unsupported("coin not configured");
        }
    }
}
