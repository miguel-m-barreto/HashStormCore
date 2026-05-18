using System;
using System.Data;
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

public class PayoutSendExecutorServiceTests : PostgresCommittedIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository payoutIntentRepo = new();

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_AcceptsTxIdAfterCommittedSendingTransition()
    {
        var poolId = NewCommittedPoolId("accepted_txid");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            await InsertBalanceAsync(con, poolId, "addr-balance", 10m, now);
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m), ("addr-b", 2m));
            var sender = new InspectingSender(GetConnectionString(), context =>
                PayoutAttemptSendResult.Accepted(new PayoutAttemptEvidence
                {
                    Kind = PayoutExternalConfirmationKinds.TxId,
                    Value = "txid-accepted"
                }));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            Assert.Equal(PayoutSendExecutionStatus.Accepted, result.Status);
            Assert.Equal("txid-accepted", result.Evidence.Value);
            Assert.Equal(1, sender.CallCount);
            Assert.True(sender.ObservedCommittedSendingState);
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, testData.Attempt.Id));
            Assert.Equal(PayoutBatchStates.Submitted, await GetBatchStateAsync(con, testData.Batch.Id));
            Assert.Equal(testData.Batch.Intents.Length, await CountIntentsAsync(con, testData.Batch.Id, PayoutIntentStates.Submitted));
            Assert.Equal(testData.Batch.Intents.Length, await CountMappingsAsync(con, testData.Attempt.Id, PayoutAttemptIntentStates.Accepted));
            Assert.Equal(1, await CountExternalConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal("txid-accepted", await GetAttemptConfirmationAsync(con, testData.Attempt.Id));
            Assert.Equal(10m, await SumBalancesAsync(con, poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "balance_changes", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_AcceptsOperationIdEvidence()
    {
        var poolId = NewCommittedPoolId("accepted_operationid");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            var sender = new StaticSender(PayoutAttemptSendResult.Accepted(new PayoutAttemptEvidence
            {
                Kind = PayoutExternalConfirmationKinds.OperationId,
                Value = "opid-accepted"
            }));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            Assert.Equal(PayoutSendExecutionStatus.Accepted, result.Status);
            Assert.Equal(PayoutExternalConfirmationKinds.OperationId, result.Evidence.Kind);
            Assert.Equal("opid-accepted", await GetAttemptOperationIdAsync(con, testData.Attempt.Id));
            Assert.Null(await GetAttemptConfirmationAsync(con, testData.Attempt.Id));
            Assert.Equal("opid-accepted", await GetBatchOperationIdAsync(con, testData.Batch.Id));
            Assert.Equal(1, await CountExternalConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.OperationId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "balance_changes", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_AcceptsAdditionalEvidenceAtomically()
    {
        var poolId = NewCommittedPoolId("accepted_extra_evidence");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            var sender = new StaticSender(PayoutAttemptSendResult.Accepted(new PayoutAttemptEvidence
            {
                Kind = PayoutExternalConfirmationKinds.TxId,
                Value = "txid-primary"
            }, new[]
            {
                new PayoutAttemptEvidence
                {
                    Kind = PayoutExternalConfirmationKinds.RawHash,
                    Value = "raw-hash-extra"
                },
                new PayoutAttemptEvidence
                {
                    Kind = PayoutExternalConfirmationKinds.WalletAck,
                    Value = "wallet-ack-extra"
                }
            }));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            Assert.Equal(PayoutSendExecutionStatus.Accepted, result.Status);
            Assert.Equal(1, await CountExternalConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(1, await CountExternalConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.RawHash));
            Assert.Equal(1, await CountExternalConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.WalletAck));
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, testData.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_FailedPreAcceptReturnsIntentsAndBatchToReserved()
    {
        var poolId = NewCommittedPoolId("failed_pre_accept");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            await InsertBalanceAsync(con, poolId, "addr-balance", 10m, now);
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m), ("addr-b", 2m));
            var sender = new StaticSender(PayoutAttemptSendResult.FailedPreAccept("pre_accept_validation", "not dispatched"));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            Assert.Equal(PayoutSendExecutionStatus.FailedPreAccept, result.Status);
            Assert.Equal("pre_accept_validation", result.ErrorCode);
            Assert.Equal(PayoutSendAttemptStates.FailedPreAccept, await GetAttemptStateAsync(con, testData.Attempt.Id));
            Assert.Equal(PayoutBatchStates.Reserved, await GetBatchStateAsync(con, testData.Batch.Id));
            Assert.Equal(testData.Batch.Intents.Length, await CountIntentsAsync(con, testData.Batch.Id, PayoutIntentStates.Reserved));
            Assert.Equal(testData.Batch.Intents.Length, await CountMappingsAsync(con, testData.Attempt.Id, PayoutAttemptIntentStates.FailedPreAccept));
            Assert.Equal(10m, await SumBalancesAsync(con, poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "balance_changes", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_AmbiguousResultMarksBatchIntentAndAttemptAmbiguous()
    {
        var poolId = NewCommittedPoolId("ambiguous");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            var sender = new StaticSender(PayoutAttemptSendResult.Ambiguous("wallet_timeout", "timeout after dispatch"));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            Assert.Equal(PayoutSendExecutionStatus.AmbiguousRequiresReview, result.Status);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, testData.Attempt.Id));
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, testData.Batch.Id));
            Assert.Equal(1, await CountIntentsAsync(con, testData.Batch.Id, PayoutIntentStates.AmbiguousRequiresReview));
            Assert.Equal(1, await CountMappingsAsync(con, testData.Attempt.Id, PayoutAttemptIntentStates.AmbiguousRequiresReview));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "balance_changes", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_UnexpectedSenderExceptionMarksAmbiguousWithoutRetry()
    {
        var poolId = NewCommittedPoolId("sender_exception");
        const string rawPayload = "RAW_RPC_PAYLOAD_SECRET";

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            var sender = new ThrowingSender(new InvalidOperationException(rawPayload));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            Assert.Equal(PayoutSendExecutionStatus.SenderFailedAmbiguous, result.Status);
            Assert.Equal("sender_exception_ambiguous", result.ErrorCode);
            Assert.Equal(nameof(InvalidOperationException), result.ErrorMessage);
            Assert.DoesNotContain(rawPayload, result.ErrorMessage);
            Assert.Equal(1, sender.CallCount);
            Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, testData.Attempt.Id));
            Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, testData.Batch.Id));
            Assert.Equal(1, await CountIntentsAsync(con, testData.Batch.Id, PayoutIntentStates.AmbiguousRequiresReview));
            Assert.Equal(nameof(InvalidOperationException), await GetAttemptErrorMessageAsync(con, testData.Attempt.Id));
            Assert.Equal(nameof(InvalidOperationException), await GetBatchErrorMessageAsync(con, testData.Batch.Id));
            Assert.DoesNotContain(rawPayload, await GetAttemptErrorMessageAsync(con, testData.Attempt.Id));
            Assert.DoesNotContain(rawPayload, await GetBatchErrorMessageAsync(con, testData.Batch.Id));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "balance_changes", poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_NullSenderResultMarksAmbiguous()
    {
        var poolId = NewCommittedPoolId("null_sender_result");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            var sender = new StaticSender(null);

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            await AssertSafetyAmbiguousAsync(con, testData, result, "sender_invalid_result_ambiguous");
            Assert.Equal(1, sender.CallCount);
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_AcceptedNullEvidenceMarksAmbiguous()
    {
        var poolId = NewCommittedPoolId("null_evidence");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            var sender = new StaticSender(new PayoutAttemptSendResult
            {
                Status = PayoutAttemptSendStatus.Accepted
            });

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            await AssertSafetyAmbiguousAsync(con, testData, result, "sender_invalid_evidence_ambiguous");
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_AcceptedUnsupportedEvidenceMarksAmbiguous()
    {
        var poolId = NewCommittedPoolId("unsupported_evidence");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            var sender = new StaticSender(PayoutAttemptSendResult.Accepted(new PayoutAttemptEvidence
            {
                Kind = "unsupported",
                Value = "value"
            }));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            await AssertSafetyAmbiguousAsync(con, testData, result, "sender_invalid_evidence_ambiguous");
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_FailedPreAcceptWithoutErrorCodeMarksAmbiguous()
    {
        var poolId = NewCommittedPoolId("invalid_failed_pre_accept");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            var sender = new StaticSender(PayoutAttemptSendResult.FailedPreAccept(" ", "missing code"));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            await AssertSafetyAmbiguousAsync(con, testData, result, "sender_invalid_result_ambiguous");
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_AmbiguousWithoutErrorCodeMarksAmbiguous()
    {
        var poolId = NewCommittedPoolId("invalid_ambiguous");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            var sender = new StaticSender(PayoutAttemptSendResult.Ambiguous("", "missing code"));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            await AssertSafetyAmbiguousAsync(con, testData, result, "sender_invalid_result_ambiguous");
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_OperationCanceledExceptionAfterClaimMarksAmbiguous()
    {
        var poolId = NewCommittedPoolId("sender_cancelled");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            var sender = new ThrowingSender(new OperationCanceledException("cancelled after dispatch"));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            await AssertSafetyAmbiguousAsync(con, testData, result, "sender_exception_ambiguous");
            Assert.Equal(1, sender.CallCount);
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_CallerCancellationAfterSenderReturnDoesNotBlockAcceptedPersistence()
    {
        var poolId = NewCommittedPoolId("post_sender_cancel");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            using var cts = new CancellationTokenSource();
            var sender = new CancellingSender(cts, PayoutAttemptSendResult.Accepted(new PayoutAttemptEvidence
            {
                Kind = PayoutExternalConfirmationKinds.TxId,
                Value = "txid-after-cancel"
            }));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, cts.Token);

            Assert.Equal(PayoutSendExecutionStatus.Accepted, result.Status);
            Assert.True(cts.IsCancellationRequested);
            Assert.Equal(PayoutSendAttemptStates.Accepted, await GetAttemptStateAsync(con, testData.Attempt.Id));
            Assert.Equal(PayoutBatchStates.Submitted, await GetBatchStateAsync(con, testData.Batch.Id));
            Assert.Equal(1, await CountExternalConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_AttemptNotFoundDoesNotCallSender()
    {
        var poolId = NewCommittedPoolId("not_found");

        return WithCommittedCleanupAsync(poolId, async _ =>
        {
            var sender = new StaticSender(PayoutAttemptSendResult.Ambiguous("should_not_call", "not expected"));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, 999999999, UtcNow()),
                sender, Ct);

            Assert.Equal(PayoutSendExecutionStatus.AttemptNotFound, result.Status);
            Assert.Equal(0, sender.CallCount);
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_AttemptNotPreparedDoesNotCallSender()
    {
        var poolId = NewCommittedPoolId("not_prepared");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var testData = await CreatePreparedAttemptAsync(con, poolId, now, ("addr-a", 1m));
            await SetAttemptStateAsync(con, testData.Attempt.Id, PayoutSendAttemptStates.Sending);
            var sender = new StaticSender(PayoutAttemptSendResult.Ambiguous("should_not_call", "not expected"));

            var result = await NewService().ExecutePreparedAttemptAsync(NewRequest(poolId, testData.Attempt.Id, now),
                sender, Ct);

            Assert.Equal(PayoutSendExecutionStatus.AttemptNotPrepared, result.Status);
            Assert.Equal(0, sender.CallCount);
            Assert.Equal(PayoutSendAttemptStates.Sending, await GetAttemptStateAsync(con, testData.Attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task ExecutePreparedAttemptAsync_RejectsInvalidRequestAndSender()
    {
        var poolId = NewCommittedPoolId("invalid");

        return WithCommittedCleanupAsync(poolId, async _ =>
        {
            var service = NewService();
            var valid = NewRequest(poolId, 1, UtcNow());
            var sender = new StaticSender(PayoutAttemptSendResult.Ambiguous("not_used", "not used"));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExecutePreparedAttemptAsync(null, sender, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExecutePreparedAttemptAsync(valid, null, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ExecutePreparedAttemptAsync(valid with { PoolId = " " }, sender, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.ExecutePreparedAttemptAsync(valid with { AttemptId = 0 }, sender, Ct));
        });
    }

    private PayoutSendExecutorService NewService()
    {
        return new PayoutSendExecutorService(new PgConnectionFactory(GetConnectionString()), payoutIntentRepo);
    }

    private async Task<TestPayoutData> CreatePreparedAttemptAsync(NpgsqlConnection con, string poolId, DateTime created,
        params (string address, decimal amount)[] intents)
    {
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            var batch = await CreateBatchAsync(con, tx, poolId, created, intents);
            var attempt = await CreateAttemptAsync(con, tx, batch, created, batch.Intents.Select(x => x.Id).ToArray());
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

        return payoutIntentRepo.CreateReservedBatchAsync(con, tx, new CreatePayoutBatchRequest
        {
            PoolId = poolId,
            Coin = Coin,
            CoinFamily = CoinFamily,
            Handler = Handler,
            SendShape = PayoutSendShapes.BatchMultiRecipient,
            RecipientSetHash = $"recipient-set-{Guid.NewGuid():N}",
            MinimumAmount = 0m,
            ReservedAmountSnapshot = intents.Sum(x => x.amount),
            IntentCountSnapshot = intents.Length,
            Created = created
        }, intentRequests, Ct);
    }

    private Task<PayoutSendAttempt> CreateAttemptAsync(NpgsqlConnection con, NpgsqlTransaction tx, PayoutBatch batch,
        DateTime created, params long[] intentIds)
    {
        var selectedIntents = batch.Intents.Where(x => intentIds.Contains(x.Id)).ToArray();

        return payoutIntentRepo.CreateSendAttemptAsync(con, tx, new CreatePayoutSendAttemptRequest
        {
            BatchId = batch.Id,
            PoolId = batch.PoolId,
            Coin = batch.Coin,
            AttemptNo = 1,
            Method = Method,
            RequestHash = $"request-hash-{Guid.NewGuid():N}",
            RequestSummary = $"test:recipients={selectedIntents.Length}",
            RecipientCount = selectedIntents.Length,
            AmountSnapshot = selectedIntents.Sum(x => x.Amount),
            Created = created
        }, intentIds, Ct);
    }

    private static PayoutSendExecutionRequest NewRequest(string poolId, long attemptId, DateTime started)
    {
        return new PayoutSendExecutionRequest
        {
            PoolId = poolId,
            AttemptId = attemptId,
            Started = started
        };
    }

    private static Task InsertBalanceAsync(NpgsqlConnection con, string poolId, string address, decimal amount, DateTime created)
    {
        return con.ExecuteAsync(@"INSERT INTO balances(poolid, address, amount, created, updated)
            VALUES(@poolid, @address, @amount, @created, @created)",
            new { poolid = poolId, address, amount, created });
    }

    private static Task SetAttemptStateAsync(NpgsqlConnection con, long attemptId, string state)
    {
        return con.ExecuteAsync("UPDATE payout_send_attempts SET state = @state WHERE id = @attemptid",
            new { attemptid = attemptId, state });
    }

    private static Task<string> GetAttemptStateAsync(NpgsqlConnection con, long attemptId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_send_attempts WHERE id = @attemptid",
            new { attemptid = attemptId });
    }

    private static Task<string> GetBatchStateAsync(NpgsqlConnection con, long batchId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_batches WHERE id = @batchid",
            new { batchid = batchId });
    }

    private static Task<string> GetAttemptOperationIdAsync(NpgsqlConnection con, long attemptId)
    {
        return con.QuerySingleOrDefaultAsync<string>("SELECT externaloperationid FROM payout_send_attempts WHERE id = @attemptid",
            new { attemptid = attemptId });
    }

    private static Task<string> GetBatchOperationIdAsync(NpgsqlConnection con, long batchId)
    {
        return con.QuerySingleOrDefaultAsync<string>("SELECT externaloperationid FROM payout_batches WHERE id = @batchid",
            new { batchid = batchId });
    }

    private static Task<string> GetAttemptConfirmationAsync(NpgsqlConnection con, long attemptId)
    {
        return con.QuerySingleOrDefaultAsync<string>(@"SELECT transactionconfirmationdata
            FROM payout_send_attempts WHERE id = @attemptid",
            new { attemptid = attemptId });
    }

    private static Task<string> GetAttemptErrorMessageAsync(NpgsqlConnection con, long attemptId)
    {
        return con.QuerySingleOrDefaultAsync<string>("SELECT errormessage FROM payout_send_attempts WHERE id = @attemptid",
            new { attemptid = attemptId });
    }

    private static Task<string> GetBatchErrorMessageAsync(NpgsqlConnection con, long batchId)
    {
        return con.QuerySingleOrDefaultAsync<string>("SELECT errormessage FROM payout_batches WHERE id = @batchid",
            new { batchid = batchId });
    }

    private static Task<int> CountIntentsAsync(NpgsqlConnection con, long batchId, string state)
    {
        return con.QuerySingleAsync<int>("SELECT COUNT(*) FROM payout_intents WHERE batchid = @batchid AND state = @state",
            new { batchid = batchId, state });
    }

    private static Task<int> CountMappingsAsync(NpgsqlConnection con, long attemptId, string state)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_attempt_intents
            WHERE attemptid = @attemptid AND state = @state",
            new { attemptid = attemptId, state });
    }

    private static Task<int> CountExternalConfirmationsAsync(NpgsqlConnection con, string poolId, string kind)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_external_confirmations
            WHERE poolid = @poolid AND kind = @kind",
            new { poolid = poolId, kind });
    }

    private static Task<decimal> SumBalancesAsync(NpgsqlConnection con, string poolId)
    {
        return con.QuerySingleAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM balances WHERE poolid = @poolid",
            new { poolid = poolId });
    }

    private static Task<int> CountPoolRowsAsync(NpgsqlConnection con, string table, string poolId)
    {
        return con.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table} WHERE poolid = @poolid", new { poolid = poolId });
    }

    private static async Task AssertSafetyAmbiguousAsync(NpgsqlConnection con, TestPayoutData testData,
        PayoutSendExecutionResult result, string expectedErrorCode)
    {
        Assert.Equal(PayoutSendExecutionStatus.SenderFailedAmbiguous, result.Status);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Equal(PayoutSendAttemptStates.AmbiguousRequiresReview, await GetAttemptStateAsync(con, testData.Attempt.Id));
        Assert.Equal(PayoutBatchStates.AmbiguousRequiresReview, await GetBatchStateAsync(con, testData.Batch.Id));
        Assert.Equal(testData.Batch.Intents.Length,
            await CountIntentsAsync(con, testData.Batch.Id, PayoutIntentStates.AmbiguousRequiresReview));
        Assert.Equal(testData.Batch.Intents.Length,
            await CountMappingsAsync(con, testData.Attempt.Id, PayoutAttemptIntentStates.AmbiguousRequiresReview));
        Assert.Equal(0, await CountPoolRowsAsync(con, "payments", testData.Batch.PoolId));
        Assert.Equal(0, await CountPoolRowsAsync(con, "balance_changes", testData.Batch.PoolId));
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private record TestPayoutData(PayoutBatch Batch, PayoutSendAttempt Attempt);

    private class StaticSender : IPayoutAttemptSender
    {
        public StaticSender(PayoutAttemptSendResult result)
        {
            this.result = result;
        }

        private readonly PayoutAttemptSendResult result;
        public int CallCount { get; private set; }

        public Task<PayoutAttemptSendResult> SendAsync(PayoutSendExecutionContext context, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private class ThrowingSender : IPayoutAttemptSender
    {
        public ThrowingSender(Exception exception)
        {
            this.exception = exception;
        }

        private readonly Exception exception;
        public int CallCount { get; private set; }

        public Task<PayoutAttemptSendResult> SendAsync(PayoutSendExecutionContext context, CancellationToken ct)
        {
            CallCount++;
            throw exception;
        }
    }

    private class CancellingSender : IPayoutAttemptSender
    {
        public CancellingSender(CancellationTokenSource cts, PayoutAttemptSendResult result)
        {
            this.cts = cts;
            this.result = result;
        }

        private readonly CancellationTokenSource cts;
        private readonly PayoutAttemptSendResult result;

        public Task<PayoutAttemptSendResult> SendAsync(PayoutSendExecutionContext context, CancellationToken ct)
        {
            cts.Cancel();
            return Task.FromResult(result);
        }
    }

    private class InspectingSender : IPayoutAttemptSender
    {
        public InspectingSender(string connectionString, Func<PayoutSendExecutionContext, PayoutAttemptSendResult> resultFactory)
        {
            this.connectionString = connectionString;
            this.resultFactory = resultFactory;
        }

        private readonly string connectionString;
        private readonly Func<PayoutSendExecutionContext, PayoutAttemptSendResult> resultFactory;
        public int CallCount { get; private set; }
        public bool ObservedCommittedSendingState { get; private set; }

        public async Task<PayoutAttemptSendResult> SendAsync(PayoutSendExecutionContext context, CancellationToken ct)
        {
            CallCount++;

            await using var con = new NpgsqlConnection(connectionString);
            await con.OpenAsync(ct);

            var attemptState = await con.QuerySingleAsync<string>(
                "SELECT state FROM payout_send_attempts WHERE id = @attemptid",
                new { attemptid = context.Attempt.Id });
            var batchState = await con.QuerySingleAsync<string>(
                "SELECT state FROM payout_batches WHERE id = @batchid",
                new { batchid = context.Batch.Id });
            var sendingIntentCount = await con.QuerySingleAsync<int>(@"SELECT COUNT(*)
                FROM payout_attempt_intents pai
                JOIN payout_intents pi ON pi.id = pai.intentid
                WHERE pai.attemptid = @attemptid AND pai.state = @mappingstate AND pi.state = @intentstate",
                new
                {
                    attemptid = context.Attempt.Id,
                    mappingstate = PayoutAttemptIntentStates.Active,
                    intentstate = PayoutIntentStates.Sending
                });

            ObservedCommittedSendingState =
                attemptState == PayoutSendAttemptStates.Sending &&
                batchState == PayoutBatchStates.Sending &&
                sendingIntentCount == context.Intents.Count;

            return resultFactory(context);
        }
    }
}
