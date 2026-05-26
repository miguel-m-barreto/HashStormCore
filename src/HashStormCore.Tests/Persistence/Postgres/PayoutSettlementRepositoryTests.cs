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

public class PayoutSettlementRepositoryTests : PostgresIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository payoutIntentRepo = new();
    private readonly PayoutSettlementRepository settlementRepo = new();

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_SettlesSingleSubmittedIntent()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settle_single"), now,
                PayoutExternalConfirmationKinds.TxId, "txid-settle-single", ("addr-a", 1.25m));
            await InsertBalanceAsync(con, tx, data.Batch.PoolId, "addr-a", 2m, now);

            var result = await settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(1)), Ct);

            Assert.Equal(PayoutSettlementStatus.Settled, result.Status);
            Assert.Equal(new[] { data.Batch.Intents[0].Id }, result.SettledIntentIds.ToArray());
            Assert.Single(result.PaymentIds);
            Assert.Single(result.BalanceChangeIds);
            Assert.Equal("txid-settle-single", result.TransactionConfirmationData);
            Assert.Equal(PayoutIntentStates.Settled, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal(PayoutBatchStates.Settled, await GetBatchStateAsync(con, tx, data.Batch.Id));
            Assert.Equal(0.75m, await GetBalanceAmountAsync(con, tx, data.Batch.PoolId, "addr-a"));
            Assert.Equal(1.25m, await GetPaymentAmountAsync(con, tx, result.PaymentIds.Single()));
            Assert.Equal(-1.25m, await GetBalanceChangeAmountAsync(con, tx, result.BalanceChangeIds.Single()));
            Assert.Equal(result.PaymentIds.Single(), await GetIntentPaymentIdAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal(result.BalanceChangeIds.Single(), await GetIntentBalanceChangeIdAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal("txid-settle-single", await GetIntentConfirmationAsync(con, tx, data.Batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_IsIdempotentForAlreadySettledAttempt()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settle_idempotent"), now,
                PayoutExternalConfirmationKinds.TxId, "txid-settle-idempotent", ("addr-a", 1m));
            await InsertBalanceAsync(con, tx, data.Batch.PoolId, "addr-a", 1m, now);

            var first = await settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(1)), Ct);
            var paymentsAfterFirst = await CountPoolRowsAsync(con, tx, "payments", data.Batch.PoolId);
            var changesAfterFirst = await CountPoolRowsAsync(con, tx, "balance_changes", data.Batch.PoolId);
            var balanceAfterFirst = await GetBalanceAmountAsync(con, tx, data.Batch.PoolId, "addr-a");

            var second = await settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(2)), Ct);

            Assert.Equal(PayoutSettlementStatus.Settled, first.Status);
            Assert.Equal(PayoutSettlementStatus.AlreadySettled, second.Status);
            Assert.Equal(first.SettledIntentIds.OrderBy(x => x).ToArray(), second.SettledIntentIds.OrderBy(x => x).ToArray());
            Assert.Equal(first.PaymentIds.OrderBy(x => x).ToArray(), second.PaymentIds.OrderBy(x => x).ToArray());
            Assert.Equal(first.BalanceChangeIds.OrderBy(x => x).ToArray(), second.BalanceChangeIds.OrderBy(x => x).ToArray());
            Assert.Equal(paymentsAfterFirst, await CountPoolRowsAsync(con, tx, "payments", data.Batch.PoolId));
            Assert.Equal(changesAfterFirst, await CountPoolRowsAsync(con, tx, "balance_changes", data.Batch.PoolId));
            Assert.Equal(balanceAfterFirst, await GetBalanceAmountAsync(con, tx, data.Batch.PoolId, "addr-a"));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_RequiresTxIdOrRawHashEvidence()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            await AssertInsufficientEvidenceAsync(con, tx, PayoutExternalConfirmationKinds.OperationId, "operation-id-only",
                "settle_operationid");
            await AssertInsufficientEvidenceAsync(con, tx, PayoutExternalConfirmationKinds.WalletAck, "wallet-ack-only",
                "settle_wallet_ack");
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_DoesNotSettleAmbiguousAttempt()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var poolId = NewPoolId("settle_ambiguous");
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m));
            var attempt = await CreateAttemptAsync(con, tx, batch, 1, "settle-ambiguous", now, batch.Intents[0].Id);
            await InsertBalanceAsync(con, tx, poolId, "addr-a", 1m, now);

            await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, now.AddMinutes(1), Ct);
            await payoutIntentRepo.MarkAttemptAmbiguousAsync(con, tx, attempt.Id, poolId, "ambiguous", null,
                now.AddMinutes(2), Ct);

            var result = await settlementRepo.SettleAcceptedAttemptAsync(con, tx,
                new PayoutSettlementRequest
                {
                    PoolId = poolId,
                    BatchId = batch.Id,
                    AttemptId = attempt.Id,
                    ExpectedEvidenceKind = PayoutExternalConfirmationKinds.TxId,
                    SettledAt = now.AddMinutes(3)
                }, Ct);

            Assert.Equal(PayoutSettlementStatus.AttemptNotEligible, result.Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, tx, attempt.Id));
            Assert.Equal(PayoutIntentStates.AmbiguousRequiresReview, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
            Assert.Equal(1m, await GetBalanceAmountAsync(con, tx, poolId, "addr-a"));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_ReturnsInsufficientBalanceAndMutatesNothing()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settle_insufficient"), now,
                PayoutExternalConfirmationKinds.TxId, "txid-settle-insufficient", ("addr-a", 2m));
            await InsertBalanceAsync(con, tx, data.Batch.PoolId, "addr-a", 1m, now);

            var result = await settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(1)), Ct);

            Assert.Equal(PayoutSettlementStatus.InsufficientBalance, result.Status);
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Null(await GetIntentPaymentIdAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Null(await GetIntentBalanceChangeIdAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal(1m, await GetBalanceAmountAsync(con, tx, data.Batch.PoolId, "addr-a"));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payments", data.Batch.PoolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "balance_changes", data.Batch.PoolId));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_MissingBalanceReturnsInsufficientBalance()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settle_missing_balance"), now,
                PayoutExternalConfirmationKinds.RawHash, "rawhash-missing-balance", ("addr-a", 1m));

            var result = await settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(1)), Ct);

            Assert.Equal(PayoutSettlementStatus.InsufficientBalance, result.Status);
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payments", data.Batch.PoolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "balance_changes", data.Batch.PoolId));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_SettlesMultiIntentAttemptAtomically()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settle_multi"), now,
                PayoutExternalConfirmationKinds.TxId, "txid-settle-multi", ("addr-a", 1m), ("addr-b", 2m));
            await InsertBalanceAsync(con, tx, data.Batch.PoolId, "addr-a", 1m, now);
            await InsertBalanceAsync(con, tx, data.Batch.PoolId, "addr-b", 3m, now);

            var result = await settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(1)), Ct);

            Assert.Equal(PayoutSettlementStatus.Settled, result.Status);
            Assert.Equal(data.Batch.Intents.Select(x => x.Id).OrderBy(x => x).ToArray(),
                result.SettledIntentIds.OrderBy(x => x).ToArray());
            Assert.Equal(2, result.PaymentIds.Count);
            Assert.Equal(2, result.BalanceChangeIds.Count);
            Assert.Equal(0m, await GetBalanceAmountAsync(con, tx, data.Batch.PoolId, "addr-a"));
            Assert.Equal(1m, await GetBalanceAmountAsync(con, tx, data.Batch.PoolId, "addr-b"));
            Assert.Equal(PayoutBatchStates.Settled, await GetBatchStateAsync(con, tx, data.Batch.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_FiltersSettlementEvidenceByExpectedKind()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settle_conflicting"), now,
                PayoutExternalConfirmationKinds.TxId, "txid-conflict-1", ("addr-a", 1m));
            await InsertBalanceAsync(con, tx, data.Batch.PoolId, "addr-a", 1m, now);
            await payoutIntentRepo.InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
            {
                PoolId = data.Batch.PoolId,
                Coin = data.Batch.Coin,
                BatchId = data.Batch.Id,
                AttemptId = data.Attempt.Id,
                Kind = PayoutExternalConfirmationKinds.RawHash,
                Value = "rawhash-conflict-2",
                Created = now.AddMinutes(1)
            }, Ct);

            var result = await settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(2)), Ct);

            Assert.Equal(PayoutSettlementStatus.Settled, result.Status);
            Assert.Equal("txid-conflict-1", result.TransactionConfirmationData);
            Assert.Equal(PayoutIntentStates.Settled, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_DoesNotSettleWrongExpectedEvidenceKind()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settle_wrong_expected_kind"), now,
                PayoutExternalConfirmationKinds.TxId, "txid-only-kind", ("addr-a", 1m));
            await InsertBalanceAsync(con, tx, data.Batch.PoolId, "addr-a", 1m, now);

            var result = await settlementRepo.SettleAcceptedAttemptAsync(con, tx,
                NewRequest(data, now.AddMinutes(1)) with
                {
                    ExpectedEvidenceKind = PayoutExternalConfirmationKinds.RawHash
                }, Ct);

            Assert.Equal(PayoutSettlementStatus.InsufficientEvidence, result.Status);
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal(1m, await GetBalanceAmountAsync(con, tx, data.Batch.PoolId, "addr-a"));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payments", data.Batch.PoolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "balance_changes", data.Batch.PoolId));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_LeavesBatchUnsettledWhenOtherIntentBlocks()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var poolId = NewPoolId("settle_batch_blocker");
            var batch = await CreateBatchAsync(con, tx, poolId, now, ("addr-a", 1m), ("addr-b", 2m));
            var first = await CreateAttemptAsync(con, tx, batch, 1, "settle-batch-blocker-1", now, batch.Intents[0].Id);
            await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, first.Id, poolId, now.AddMinutes(1), Ct);
            await payoutIntentRepo.MarkAttemptAcceptedAsync(con, tx, first.Id, poolId,
                NewEvidence(PayoutExternalConfirmationKinds.TxId, "txid-batch-blocker"), now.AddMinutes(2), Ct);
            await InsertBalanceAsync(con, tx, poolId, "addr-a", 1m, now);

            var result = await settlementRepo.SettleAcceptedAttemptAsync(con, tx, new PayoutSettlementRequest
            {
                PoolId = poolId,
                BatchId = batch.Id,
                AttemptId = first.Id,
                ExpectedEvidenceKind = PayoutExternalConfirmationKinds.TxId,
                SettledAt = now.AddMinutes(3)
            }, Ct);

            Assert.Equal(PayoutSettlementStatus.Settled, result.Status);
            Assert.Equal(PayoutIntentStates.Settled, await GetIntentStateAsync(con, tx, batch.Intents[0].Id));
            Assert.Equal(PayoutIntentStates.Reserved, await GetIntentStateAsync(con, tx, batch.Intents[1].Id));
            Assert.Equal(PayoutBatchStates.Sending, await GetBatchStateAsync(con, tx, batch.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_ThrowsOnPartialAccountingLinks()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settle_partial_link"), now,
                PayoutExternalConfirmationKinds.TxId, "txid-partial-link", ("addr-a", 1m));
            await InsertBalanceAsync(con, tx, data.Batch.PoolId, "addr-a", 1m, now);
            var paymentId = await InsertPaymentAsync(con, tx, data.Batch.PoolId, data.Batch.Coin, "addr-a", 1m,
                "txid-partial-link", now);
            await con.ExecuteAsync("UPDATE payout_intents SET paymentid = @paymentid WHERE id = @intentid",
                new { paymentid = paymentId, intentid = data.Batch.Intents[0].Id }, tx);
            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", data.Batch.PoolId);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(1)), Ct));

            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", data.Batch.PoolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "balance_changes", data.Batch.PoolId));
            Assert.Equal(1m, await GetBalanceAmountAsync(con, tx, data.Batch.PoolId, "addr-a"));
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_ThrowsOnSubmittedIntentWithConfirmationData()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settle_stale_confirmation"), now,
                PayoutExternalConfirmationKinds.TxId, "txid-stale-confirmation", ("addr-a", 1m));
            await InsertBalanceAsync(con, tx, data.Batch.PoolId, "addr-a", 1m, now);
            await con.ExecuteAsync(@"UPDATE payout_intents
                SET transactionconfirmationdata = @confirmation
                WHERE id = @intentid",
                new { confirmation = "corrupt-confirmation", intentid = data.Batch.Intents[0].Id }, tx);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(1)), Ct));

            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payments", data.Batch.PoolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, tx, "balance_changes", data.Batch.PoolId));
            Assert.Equal(1m, await GetBalanceAmountAsync(con, tx, data.Batch.PoolId, "addr-a"));
            Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Null(await GetIntentPaymentIdAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Null(await GetIntentBalanceChangeIdAsync(con, tx, data.Batch.Intents[0].Id));
            Assert.Equal("corrupt-confirmation", await GetIntentConfirmationAsync(con, tx, data.Batch.Intents[0].Id));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_SettlesRewardRecipientLikeAddress()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var now = UtcNow();
            var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId("settle_reward_recipient"), now,
                PayoutExternalConfirmationKinds.TxId, "txid-reward-recipient", ("reward-recipient-address", 1m));
            await InsertBalanceAsync(con, tx, data.Batch.PoolId, "reward-recipient-address", 1m, now);

            var result = await settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(1)), Ct);

            Assert.Equal(PayoutSettlementStatus.Settled, result.Status);
            Assert.Single(result.PaymentIds);
            Assert.Equal("reward-recipient-address", await GetPaymentAddressAsync(con, tx, result.PaymentIds.Single()));
        });
    }

    [PostgresIntegrationFact]
    public Task SettleAcceptedAttemptAsync_RequiresTransactionAndValidArguments()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var request = new PayoutSettlementRequest
            {
                PoolId = "pool",
                BatchId = 1,
                AttemptId = 1,
                ExpectedEvidenceKind = PayoutExternalConfirmationKinds.TxId,
                SettledAt = UtcNow()
            };

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                settlementRepo.SettleAcceptedAttemptAsync(null, tx, request, Ct));
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                settlementRepo.SettleAcceptedAttemptAsync(con, null, request, Ct));
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                settlementRepo.SettleAcceptedAttemptAsync(con, tx, null, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                settlementRepo.SettleAcceptedAttemptAsync(con, tx, request with { PoolId = " " }, Ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                settlementRepo.SettleAcceptedAttemptAsync(con, tx, request with { BatchId = 0 }, Ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                settlementRepo.SettleAcceptedAttemptAsync(con, tx, request with { AttemptId = 0 }, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                settlementRepo.SettleAcceptedAttemptAsync(con, tx, request with { ExpectedEvidenceKind = " " }, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                settlementRepo.SettleAcceptedAttemptAsync(con, tx,
                    request with { ExpectedEvidenceKind = PayoutExternalConfirmationKinds.OperationId }, Ct));
        });
    }

    private async Task AssertInsufficientEvidenceAsync(NpgsqlConnection con, NpgsqlTransaction tx, string evidenceKind,
        string evidenceValue, string poolSuffix)
    {
        var now = UtcNow();
        var data = await CreateAcceptedAttemptAsync(con, tx, NewPoolId(poolSuffix), now, evidenceKind, evidenceValue,
            ("addr-a", 1m));
        await InsertBalanceAsync(con, tx, data.Batch.PoolId, "addr-a", 1m, now);

        var result = await settlementRepo.SettleAcceptedAttemptAsync(con, tx, NewRequest(data, now.AddMinutes(1)), Ct);

        Assert.Equal(PayoutSettlementStatus.InsufficientEvidence, result.Status);
        Assert.Equal(PayoutIntentStates.Submitted, await GetIntentStateAsync(con, tx, data.Batch.Intents[0].Id));
        Assert.Equal(1m, await GetBalanceAmountAsync(con, tx, data.Batch.PoolId, "addr-a"));
        Assert.Equal(0, await CountPoolRowsAsync(con, tx, "payments", data.Batch.PoolId));
        Assert.Equal(0, await CountPoolRowsAsync(con, tx, "balance_changes", data.Batch.PoolId));
    }

    private async Task<TestPayoutData> CreateAcceptedAttemptAsync(NpgsqlConnection con, NpgsqlTransaction tx,
        string poolId, DateTime created, string evidenceKind, string evidenceValue,
        params (string address, decimal amount)[] intents)
    {
        var batch = await CreateBatchAsync(con, tx, poolId, created, intents);
        var attempt = await CreateAttemptAsync(con, tx, batch, 1, $"settle-attempt-{Guid.NewGuid():N}", created,
            batch.Intents.Select(x => x.Id).ToArray());
        await payoutIntentRepo.MarkAttemptSendingAsync(con, tx, attempt.Id, poolId, created.AddMinutes(1), Ct);
        var acceptedEvidence = evidenceKind == PayoutExternalConfirmationKinds.OperationId
            ? NewEvidence(PayoutExternalConfirmationKinds.OperationId, evidenceValue)
            : NewEvidence(PayoutExternalConfirmationKinds.OperationId, $"operation-{Guid.NewGuid():N}");
        await payoutIntentRepo.MarkAttemptAcceptedAsync(con, tx, attempt.Id, poolId,
            acceptedEvidence, created.AddMinutes(2), Ct);

        if(evidenceKind != PayoutExternalConfirmationKinds.OperationId)
        {
            await payoutIntentRepo.InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
            {
                PoolId = batch.PoolId,
                Coin = batch.Coin,
                BatchId = batch.Id,
                AttemptId = attempt.Id,
                Kind = evidenceKind,
                Value = evidenceValue,
                Created = created.AddMinutes(3)
            }, Ct);
        }

        return new TestPayoutData(batch, attempt, evidenceKind);
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
            RecipientSetHash = $"settlement-recipient-set-{Guid.NewGuid():N}",
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

    private static PayoutSettlementRequest NewRequest(TestPayoutData data, DateTime settledAt)
    {
        return new PayoutSettlementRequest
        {
            PoolId = data.Batch.PoolId,
            BatchId = data.Batch.Id,
            AttemptId = data.Attempt.Id,
            ExpectedEvidenceKind = GetFinalEvidenceKindOrTxId(data.EvidenceKind),
            SettledAt = settledAt
        };
    }

    private static string GetFinalEvidenceKindOrTxId(string evidenceKind)
    {
        return evidenceKind == PayoutExternalConfirmationKinds.RawHash
            ? PayoutExternalConfirmationKinds.RawHash
            : PayoutExternalConfirmationKinds.TxId;
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

    private static Task<long> InsertPaymentAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId, string coin,
        string address, decimal amount, string transactionConfirmationData, DateTime created)
    {
        return con.QuerySingleAsync<long>(@"INSERT INTO payments(poolid, coin, address, amount, transactionconfirmationdata, created)
            VALUES(@poolid, @coin, @address, @amount, @transactionconfirmationdata, @created)
            RETURNING id",
            new { poolid = poolId, coin, address, amount, transactionconfirmationdata = transactionConfirmationData, created }, tx);
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

    private static Task<long?> GetIntentPaymentIdAsync(NpgsqlConnection con, NpgsqlTransaction tx, long intentId)
    {
        return con.QuerySingleAsync<long?>("SELECT paymentid FROM payout_intents WHERE id = @intentid",
            new { intentid = intentId }, tx);
    }

    private static Task<long?> GetIntentBalanceChangeIdAsync(NpgsqlConnection con, NpgsqlTransaction tx, long intentId)
    {
        return con.QuerySingleAsync<long?>("SELECT balancechangeid FROM payout_intents WHERE id = @intentid",
            new { intentid = intentId }, tx);
    }

    private static Task<string> GetIntentConfirmationAsync(NpgsqlConnection con, NpgsqlTransaction tx, long intentId)
    {
        return con.QuerySingleAsync<string>("SELECT transactionconfirmationdata FROM payout_intents WHERE id = @intentid",
            new { intentid = intentId }, tx);
    }

    private static Task<decimal> GetBalanceAmountAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId,
        string address)
    {
        return con.QuerySingleAsync<decimal>("SELECT amount FROM balances WHERE poolid = @poolid AND address = @address",
            new { poolid = poolId, address }, tx);
    }

    private static Task<decimal> GetPaymentAmountAsync(NpgsqlConnection con, NpgsqlTransaction tx, long paymentId)
    {
        return con.QuerySingleAsync<decimal>("SELECT amount FROM payments WHERE id = @paymentid",
            new { paymentid = paymentId }, tx);
    }

    private static Task<string> GetPaymentAddressAsync(NpgsqlConnection con, NpgsqlTransaction tx, long paymentId)
    {
        return con.QuerySingleAsync<string>("SELECT address FROM payments WHERE id = @paymentid",
            new { paymentid = paymentId }, tx);
    }

    private static Task<decimal> GetBalanceChangeAmountAsync(NpgsqlConnection con, NpgsqlTransaction tx,
        long balanceChangeId)
    {
        return con.QuerySingleAsync<decimal>("SELECT amount FROM balance_changes WHERE id = @balancechangeid",
            new { balancechangeid = balanceChangeId }, tx);
    }

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, IDbTransaction tx, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid", new { poolid = poolId }, tx);
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private record TestPayoutData(PayoutBatch Batch, PayoutSendAttempt Attempt, string EvidenceKind);
}
