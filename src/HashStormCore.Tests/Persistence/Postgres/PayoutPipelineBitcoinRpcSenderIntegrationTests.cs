using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HashStormCore.Payments;
using HashStormCore.PayoutProcessor.Configuration;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Postgres;
using HashStormCore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace HashStormCore.Tests.Persistence.Postgres;

public class PayoutPipelineBitcoinRpcSenderIntegrationTests : PostgresCommittedIntegrationTestBase
{
    private const string Coin = "bitcoin";
    private const string PoolCoin = "bitcoin";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository payoutIntentRepo = new();
    private readonly PayoutReservationRepository payoutReservationRepo = new();
    private readonly PayoutSettlementRepository payoutSettlementRepo = new();

    [PostgresIntegrationFact]
    public Task ConfiguredBitcoinRpcSenderExecutesAndSettlesAcceptedTxId()
    {
        var poolId = NewCommittedPoolId("bitcoin_rpc_pipeline_accept");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var httpHandler = new FakeBitcoinRpcHttpHandler(
                "{\"result\":\"txid-accepted-1\",\"error\":null,\"id\":\"1\"}");
            var pipeline = NewPipeline(poolId, httpHandler);
            await InsertBalanceAsync(con, poolId, "addr-one", 3m, now.AddSeconds(-2));
            await InsertBalanceAsync(con, poolId, "addr-two", 4m, now.AddSeconds(-1));
            var balanceBefore = await SumBalancesAsync(con, poolId);

            var reservation = await pipeline.ReservationRunner.CreateReservationAsync(new CreatePayoutReservationRequest
            {
                PoolId = poolId,
                Coin = Coin,
                CoinFamily = PayoutProfileConstants.Families.Bitcoin,
                Handler = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                MinimumPayment = 1m,
                MaxCandidates = 10,
                Created = now
            }, Ct);

            var planning = await pipeline.PlanningRunner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
            {
                PoolId = poolId,
                MaxBatches = 10,
                Created = now.AddSeconds(1)
            }, Ct);

            var execution = await pipeline.ExecutionRunner.ExecutePreparedAttemptsAsync(new PayoutExecutionRunnerRequest
            {
                PoolId = poolId,
                MaxAttempts = 10,
                Started = now.AddSeconds(2)
            }, Ct);

            var stale = await pipeline.StaleRunner.ReconcileStaleSendingAsync(
                NewStaleRequest(poolId, now.AddMinutes(-30), now.AddSeconds(3)), Ct);

            var operationId = await pipeline.OperationIdRunner.ReconcileOperationIdsAsync(
                new PayoutOperationIdReconciliationRunnerRequest
                {
                    PoolId = poolId,
                    Limit = 10,
                    CheckedAt = now.AddSeconds(4)
                }, Ct);

            var settlement = await pipeline.SettlementRunner.SettleAcceptedAttemptsAsync(
                new PayoutSettlementRunnerRequest
                {
                    PoolId = poolId,
                    Limit = 10,
                    SettledAt = now.AddSeconds(5)
                }, Ct);

            Assert.Equal(PayoutReservationStatus.Created, reservation.Status);
            Assert.Equal(1, planning.PlannedBatchCount);
            Assert.Equal(1, planning.Results.Single().Attempts.Count);
            Assert.Equal(1, execution.CandidateAttemptCount);
            Assert.Equal(1, execution.ExecutedAttemptCount);
            Assert.Equal(0, execution.SkippedAttemptCount);
            Assert.Equal(0, execution.FailureCount);
            Assert.Equal(PayoutSendExecutionStatus.Accepted, Assert.Single(execution.ExecutionResults).Status);
            Assert.Equal(1, httpHandler.CallCount);
            Assert.Equal(HttpMethod.Post, httpHandler.LastMethod);

            using(var requestBody = JsonDocument.Parse(httpHandler.LastRequestBody))
            {
                Assert.Equal("sendmany", requestBody.RootElement.GetProperty("method").GetString());
                var recipients = requestBody.RootElement.GetProperty("params")[1];
                Assert.Equal(3m, recipients.GetProperty("addr-one").GetDecimal());
                Assert.Equal(4m, recipients.GetProperty("addr-two").GetDecimal());
            }

            Assert.Equal(0, stale.CandidateBatchCount);
            Assert.Empty(stale.MarkedBatchIds);
            Assert.Equal(0, operationId.CandidateCount);
            Assert.Equal(0, operationId.EvidenceAttachedCount);
            Assert.Equal(1, settlement.CandidateCount);
            Assert.Equal(1, settlement.SettledCount);
            Assert.Equal(0, settlement.SkippedCount);
            Assert.Equal(0, settlement.FailureCount);

            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.Prepared));
            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.Sending));
            Assert.Equal(1, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.Accepted));
            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.AmbiguousRequiresReview));
            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.FailedPreAccept));
            Assert.Equal(1, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.RawHash));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.OperationId));
            Assert.Equal("txid-accepted-1", await GetConfirmationValueAsync(con, poolId,
                PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(2, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(2, await CountNegativeBalanceChangesAsync(con, poolId));
            Assert.Equal(2, await CountIntentsInStateAsync(con, poolId, PayoutIntentStates.Settled));
            Assert.Equal(0, await CountIntentsInStateAsync(con, poolId, PayoutIntentStates.Submitted));
            Assert.Equal(0m, await SumBalancesAsync(con, poolId));
            Assert.Equal(balanceBefore, await SumPaymentsAsync(con, poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task WalletLockedThenUnlocksAndSettles()
    {
        var poolId = NewCommittedPoolId("bitcoin_rpc_pipeline_unlock_accept");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            const string walletPassphrase = "SECRET_WALLET_PASSPHRASE_TOKEN";
            var now = UtcNow();
            var httpHandler = new FakeBitcoinRpcHttpHandler(new object[]
            {
                "{\"result\":null,\"error\":{\"code\":-13,\"message\":\"wallet locked\"},\"id\":\"1\"}",
                "{\"result\":null,\"error\":null,\"id\":\"2\"}",
                "{\"result\":\"txid-after-unlock\",\"error\":null,\"id\":\"3\"}",
                "{\"result\":null,\"error\":null,\"id\":\"4\"}"
            });
            var pipeline = NewPipeline(poolId, httpHandler, walletPassphrase: walletPassphrase,
                walletUnlockSeconds: 60);
            await InsertBalanceAsync(con, poolId, "addr-one", 3m, now.AddSeconds(-2));
            await InsertBalanceAsync(con, poolId, "addr-two", 4m, now.AddSeconds(-1));

            await pipeline.ReservationRunner.CreateReservationAsync(new CreatePayoutReservationRequest
            {
                PoolId = poolId,
                Coin = Coin,
                CoinFamily = PayoutProfileConstants.Families.Bitcoin,
                Handler = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                MinimumPayment = 1m,
                MaxCandidates = 10,
                Created = now
            }, Ct);

            await pipeline.PlanningRunner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
            {
                PoolId = poolId,
                MaxBatches = 10,
                Created = now.AddSeconds(1)
            }, Ct);

            var execution = await pipeline.ExecutionRunner.ExecutePreparedAttemptsAsync(new PayoutExecutionRunnerRequest
            {
                PoolId = poolId,
                MaxAttempts = 10,
                Started = now.AddSeconds(2)
            }, Ct);

            var settlement = await pipeline.SettlementRunner.SettleAcceptedAttemptsAsync(
                new PayoutSettlementRunnerRequest
                {
                    PoolId = poolId,
                    Limit = 10,
                    SettledAt = now.AddSeconds(5)
                }, Ct);

            Assert.Equal(PayoutSendExecutionStatus.Accepted, Assert.Single(execution.ExecutionResults).Status);
            Assert.Equal(new[] { "sendmany", "walletpassphrase", "sendmany", "walletlock" },
                httpHandler.Methods.ToArray());
            using(var unlock = JsonDocument.Parse(httpHandler.RequestBodies[1]))
            {
                var parameters = unlock.RootElement.GetProperty("params");
                Assert.Equal(walletPassphrase, parameters[0].GetString());
                Assert.Equal(60, parameters[1].GetInt32());
            }

            Assert.Equal(1, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.Accepted));
            Assert.Equal(1, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal("txid-after-unlock", await GetConfirmationValueAsync(con, poolId,
                PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(1, settlement.CandidateCount);
            Assert.Equal(1, settlement.SettledCount);
            Assert.Equal(2, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(2, await CountNegativeBalanceChangesAsync(con, poolId));
            Assert.Equal(2, await CountIntentsInStateAsync(con, poolId, PayoutIntentStates.Settled));
        });
    }

    [PostgresIntegrationFact]
    public Task WalletLockedWithoutPassphraseFailsPreAcceptNoDebit()
    {
        var poolId = NewCommittedPoolId("bitcoin_rpc_pipeline_locked_no_passphrase");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            var now = UtcNow();
            var httpHandler = new FakeBitcoinRpcHttpHandler(
                "{\"result\":null,\"error\":{\"code\":-13,\"message\":\"wallet locked\"},\"id\":\"1\"}");
            var pipeline = NewPipeline(poolId, httpHandler);
            await InsertBalanceAsync(con, poolId, "addr-one", 3m, now.AddSeconds(-2));
            await InsertBalanceAsync(con, poolId, "addr-two", 4m, now.AddSeconds(-1));
            var balanceBefore = await SumBalancesAsync(con, poolId);

            await pipeline.ReservationRunner.CreateReservationAsync(new CreatePayoutReservationRequest
            {
                PoolId = poolId,
                Coin = Coin,
                CoinFamily = PayoutProfileConstants.Families.Bitcoin,
                Handler = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                MinimumPayment = 1m,
                MaxCandidates = 10,
                Created = now
            }, Ct);

            await pipeline.PlanningRunner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
            {
                PoolId = poolId,
                MaxBatches = 10,
                Created = now.AddSeconds(1)
            }, Ct);

            var execution = await pipeline.ExecutionRunner.ExecutePreparedAttemptsAsync(new PayoutExecutionRunnerRequest
            {
                PoolId = poolId,
                MaxAttempts = 10,
                Started = now.AddSeconds(2)
            }, Ct);

            var settlement = await pipeline.SettlementRunner.SettleAcceptedAttemptsAsync(
                new PayoutSettlementRunnerRequest
                {
                    PoolId = poolId,
                    Limit = 10,
                    SettledAt = now.AddSeconds(5)
                }, Ct);

            var executionResult = Assert.Single(execution.ExecutionResults);
            Assert.Equal(PayoutSendExecutionStatus.FailedPreAccept, executionResult.Status);
            Assert.Equal("bitcoin_wallet_locked", executionResult.ErrorCode);
            Assert.Equal(new[] { "sendmany" }, httpHandler.Methods.ToArray());
            Assert.Equal(1, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.FailedPreAccept));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(0, settlement.CandidateCount);
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountNegativeBalanceChangesAsync(con, poolId));
            Assert.Equal(balanceBefore, await SumBalancesAsync(con, poolId));
        });
    }

    [PostgresIntegrationFact]
    public Task WalletUnlockTransportFailureAmbiguousNoDebit()
    {
        var poolId = NewCommittedPoolId("bitcoin_rpc_pipeline_unlock_failure");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            const string endpoint = "http://SECRET_ENDPOINT_TOKEN.invalid:18443";
            const string username = "SECRET_USERNAME_TOKEN";
            const string password = "SECRET_PASSWORD_TOKEN";
            const string walletName = "SECRET_WALLET_TOKEN";
            const string walletPassphrase = "SECRET_WALLET_PASSPHRASE_TOKEN";
            var now = UtcNow();
            var httpHandler = new FakeBitcoinRpcHttpHandler(new object[]
            {
                "{\"result\":null,\"error\":{\"code\":-13,\"message\":\"wallet locked\"},\"id\":\"1\"}",
                new HttpRequestException("unlock failed SECRET_WALLET_PASSPHRASE_TOKEN")
            });
            var pipeline = NewPipeline(poolId, httpHandler, endpoint, username, password, walletName,
                walletPassphrase, 60);
            await InsertBalanceAsync(con, poolId, "addr-one", 3m, now.AddSeconds(-2));
            await InsertBalanceAsync(con, poolId, "addr-two", 4m, now.AddSeconds(-1));
            var balanceBefore = await SumBalancesAsync(con, poolId);

            await pipeline.ReservationRunner.CreateReservationAsync(new CreatePayoutReservationRequest
            {
                PoolId = poolId,
                Coin = Coin,
                CoinFamily = PayoutProfileConstants.Families.Bitcoin,
                Handler = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                MinimumPayment = 1m,
                MaxCandidates = 10,
                Created = now
            }, Ct);

            await pipeline.PlanningRunner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
            {
                PoolId = poolId,
                MaxBatches = 10,
                Created = now.AddSeconds(1)
            }, Ct);

            var execution = await pipeline.ExecutionRunner.ExecutePreparedAttemptsAsync(new PayoutExecutionRunnerRequest
            {
                PoolId = poolId,
                MaxAttempts = 10,
                Started = now.AddSeconds(2)
            }, Ct);

            var settlement = await pipeline.SettlementRunner.SettleAcceptedAttemptsAsync(
                new PayoutSettlementRunnerRequest
                {
                    PoolId = poolId,
                    Limit = 10,
                    SettledAt = now.AddSeconds(5)
                }, Ct);

            var executionResult = Assert.Single(execution.ExecutionResults);
            Assert.Equal(PayoutSendExecutionStatus.AmbiguousRequiresReview, executionResult.Status);
            Assert.Equal("bitcoin_wallet_unlock_ambiguous", executionResult.ErrorCode);
            Assert.Equal(new[] { "sendmany", "walletpassphrase" }, httpHandler.Methods.ToArray());
            Assert.Equal(1, await CountAttemptsInStateAsync(con, poolId,
                PayoutSendAttemptStates.AmbiguousRequiresReview));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(0, settlement.CandidateCount);
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountNegativeBalanceChangesAsync(con, poolId));
            Assert.Equal(balanceBefore, await SumBalancesAsync(con, poolId));

            var errorCode = await GetOnlyAttemptErrorCodeAsync(con, poolId);
            var errorMessage = await GetOnlyAttemptErrorMessageAsync(con, poolId);
            AssertDoesNotLeak(errorCode, endpoint, username, password, walletName, walletPassphrase,
                "Authorization", "Basic", "SECRET_ENDPOINT_TOKEN", "SECRET_USERNAME_TOKEN",
                "SECRET_PASSWORD_TOKEN", "SECRET_WALLET_TOKEN", "SECRET_WALLET_PASSPHRASE_TOKEN");
            AssertDoesNotLeak(errorMessage, endpoint, username, password, walletName, walletPassphrase,
                "Authorization", "Basic", "SECRET_ENDPOINT_TOKEN", "SECRET_USERNAME_TOKEN",
                "SECRET_PASSWORD_TOKEN", "SECRET_WALLET_TOKEN", "SECRET_WALLET_PASSPHRASE_TOKEN");
        });
    }

    [PostgresIntegrationFact]
    public Task BitcoinRpcJsonRpcErrorFailsPreAcceptAndDoesNotSettleOrDebit()
    {
        var poolId = NewCommittedPoolId("bitcoin_rpc_pipeline_error");

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            const string endpoint = "http://127.0.0.1:18443";
            const string username = "rpc-user";
            const string password = "rpc-password";
            const string walletName = "secret-wallet";
            var now = UtcNow();
            var httpHandler = new FakeBitcoinRpcHttpHandler(
                "{\"result\":null,\"error\":{\"code\":-6,\"message\":\"Insufficient funds\"},\"id\":\"1\"}");
            var pipeline = NewPipeline(poolId, httpHandler, endpoint, username, password, walletName);
            await InsertBalanceAsync(con, poolId, "addr-one", 3m, now.AddSeconds(-2));
            await InsertBalanceAsync(con, poolId, "addr-two", 4m, now.AddSeconds(-1));
            var balanceBefore = await SumBalancesAsync(con, poolId);

            await pipeline.ReservationRunner.CreateReservationAsync(new CreatePayoutReservationRequest
            {
                PoolId = poolId,
                Coin = Coin,
                CoinFamily = PayoutProfileConstants.Families.Bitcoin,
                Handler = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                MinimumPayment = 1m,
                MaxCandidates = 10,
                Created = now
            }, Ct);

            await pipeline.PlanningRunner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
            {
                PoolId = poolId,
                MaxBatches = 10,
                Created = now.AddSeconds(1)
            }, Ct);

            var execution = await pipeline.ExecutionRunner.ExecutePreparedAttemptsAsync(new PayoutExecutionRunnerRequest
            {
                PoolId = poolId,
                MaxAttempts = 10,
                Started = now.AddSeconds(2)
            }, Ct);

            var settlement = await pipeline.SettlementRunner.SettleAcceptedAttemptsAsync(
                new PayoutSettlementRunnerRequest
                {
                    PoolId = poolId,
                    Limit = 10,
                    SettledAt = now.AddSeconds(5)
                }, Ct);

            Assert.Equal(1, execution.CandidateAttemptCount);
            Assert.Equal(1, execution.ExecutedAttemptCount);
            Assert.Equal(PayoutSendExecutionStatus.FailedPreAccept, Assert.Single(execution.ExecutionResults).Status);
            Assert.Equal(1, httpHandler.CallCount);
            Assert.Equal(1, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.FailedPreAccept));
            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.AmbiguousRequiresReview));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(0, settlement.CandidateCount);
            Assert.Equal(0, settlement.SettledCount);
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountNegativeBalanceChangesAsync(con, poolId));
            Assert.Equal(balanceBefore, await SumBalancesAsync(con, poolId));
            Assert.Equal(0, await CountIntentsInStateAsync(con, poolId, PayoutIntentStates.Settled));

            var errorCode = await GetOnlyAttemptErrorCodeAsync(con, poolId);
            var errorMessage = await GetOnlyAttemptErrorMessageAsync(con, poolId);
            Assert.Equal("bitcoin_jsonrpc_error", errorCode);
            Assert.Contains("-6", errorMessage, StringComparison.Ordinal);
            AssertDoesNotLeak(errorCode, endpoint, username, password, walletName, "Authorization", "Basic");
            AssertDoesNotLeak(errorMessage, endpoint, username, password, walletName, "Authorization", "Basic");
        });
    }

    [PostgresIntegrationFact]
    public Task BitcoinRpcHttp500EmptyBodyMarksAmbiguousAndDoesNotSettleOrDebit()
    {
        return RunAmbiguousFailureCaseAsync("bitcoin_rpc_pipeline_500_empty",
            new FakeBitcoinRpcHttpHandler(string.Empty, HttpStatusCode.InternalServerError),
            PayoutSendExecutionStatus.AmbiguousRequiresReview,
            "bitcoin_jsonrpc_invalid_response",
            "could not be safely interpreted");
    }

    [PostgresIntegrationFact]
    public Task BitcoinRpcInvalidJsonMarksAmbiguousAndDoesNotSettleOrDebit()
    {
        return RunAmbiguousFailureCaseAsync("bitcoin_rpc_pipeline_invalid_json",
            new FakeBitcoinRpcHttpHandler("not-json"),
            PayoutSendExecutionStatus.AmbiguousRequiresReview,
            "bitcoin_jsonrpc_invalid_response",
            "could not be safely interpreted");
    }

    [PostgresIntegrationFact]
    public Task BitcoinRpcNullResultMarksAmbiguousAndDoesNotSettleOrDebit()
    {
        return RunAmbiguousFailureCaseAsync("bitcoin_rpc_pipeline_null_result",
            new FakeBitcoinRpcHttpHandler("{\"result\":null,\"error\":null,\"id\":\"1\"}"),
            PayoutSendExecutionStatus.AmbiguousRequiresReview,
            "bitcoin_jsonrpc_invalid_response",
            "could not be safely interpreted");
    }

    [PostgresIntegrationFact]
    public Task BitcoinRpcMissingResultMarksAmbiguousAndDoesNotSettleOrDebit()
    {
        return RunAmbiguousFailureCaseAsync("bitcoin_rpc_pipeline_missing_result",
            new FakeBitcoinRpcHttpHandler("{\"error\":null,\"id\":\"1\"}"),
            PayoutSendExecutionStatus.AmbiguousRequiresReview,
            "bitcoin_jsonrpc_invalid_response",
            "could not be safely interpreted");
    }

    [PostgresIntegrationFact]
    public Task BitcoinRpcNonStringResultMarksAmbiguousAndDoesNotSettleOrDebit()
    {
        return RunAmbiguousFailureCaseAsync("bitcoin_rpc_pipeline_object_result",
            new FakeBitcoinRpcHttpHandler("{\"result\":{\"txid\":\"abc\"},\"error\":null,\"id\":\"1\"}"),
            PayoutSendExecutionStatus.AmbiguousRequiresReview,
            "bitcoin_jsonrpc_invalid_response",
            "could not be safely interpreted");
    }

    [PostgresIntegrationFact]
    public Task BitcoinRpcTransportExceptionMarksAmbiguousAndDoesNotSettleOrDebit()
    {
        return RunAmbiguousFailureCaseAsync("bitcoin_rpc_pipeline_transport_exception",
            new FakeBitcoinRpcHttpHandler(new HttpRequestException("transport failed SECRET_ENDPOINT_TOKEN")),
            PayoutSendExecutionStatus.SenderFailedAmbiguous,
            "sender_exception_ambiguous",
            nameof(HttpRequestException));
    }

    [PostgresIntegrationFact]
    public Task BitcoinRpcTransportTaskCanceledMarksAmbiguousAndDoesNotSettleOrDebit()
    {
        return RunAmbiguousFailureCaseAsync("bitcoin_rpc_pipeline_task_canceled",
            new FakeBitcoinRpcHttpHandler(new TaskCanceledException("timeout SECRET_ENDPOINT_TOKEN")),
            PayoutSendExecutionStatus.SenderFailedAmbiguous,
            "sender_exception_ambiguous",
            nameof(TaskCanceledException));
    }

    private Task RunAmbiguousFailureCaseAsync(
        string poolPrefix,
        FakeBitcoinRpcHttpHandler httpHandler,
        PayoutSendExecutionStatus expectedExecutionStatus,
        string expectedErrorCode,
        string expectedErrorMessageToken)
    {
        var poolId = NewCommittedPoolId(poolPrefix);

        return WithCommittedCleanupAsync(poolId, async con =>
        {
            const string endpoint = "http://SECRET_ENDPOINT_TOKEN.invalid:18443";
            const string username = "SECRET_USERNAME_TOKEN";
            const string password = "SECRET_PASSWORD_TOKEN";
            const string walletName = "SECRET_WALLET_TOKEN";
            var now = UtcNow();
            var pipeline = NewPipeline(poolId, httpHandler, endpoint, username, password, walletName);
            await InsertBalanceAsync(con, poolId, "addr-one", 3m, now.AddSeconds(-2));
            await InsertBalanceAsync(con, poolId, "addr-two", 4m, now.AddSeconds(-1));
            var balanceBefore = await SumBalancesAsync(con, poolId);

            await pipeline.ReservationRunner.CreateReservationAsync(new CreatePayoutReservationRequest
            {
                PoolId = poolId,
                Coin = Coin,
                CoinFamily = PayoutProfileConstants.Families.Bitcoin,
                Handler = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                MinimumPayment = 1m,
                MaxCandidates = 10,
                Created = now
            }, Ct);

            await pipeline.PlanningRunner.CreateSendAttemptsAsync(new PayoutPlanningRunnerRequest
            {
                PoolId = poolId,
                MaxBatches = 10,
                Created = now.AddSeconds(1)
            }, Ct);

            var execution = await pipeline.ExecutionRunner.ExecutePreparedAttemptsAsync(new PayoutExecutionRunnerRequest
            {
                PoolId = poolId,
                MaxAttempts = 10,
                Started = now.AddSeconds(2)
            }, Ct);

            var stale = await pipeline.StaleRunner.ReconcileStaleSendingAsync(
                NewStaleRequest(poolId, now.AddMinutes(-30), now.AddSeconds(3)), Ct);

            var operationId = await pipeline.OperationIdRunner.ReconcileOperationIdsAsync(
                new PayoutOperationIdReconciliationRunnerRequest
                {
                    PoolId = poolId,
                    Limit = 10,
                    CheckedAt = now.AddSeconds(4)
                }, Ct);

            var settlement = await pipeline.SettlementRunner.SettleAcceptedAttemptsAsync(
                new PayoutSettlementRunnerRequest
                {
                    PoolId = poolId,
                    Limit = 10,
                    SettledAt = now.AddSeconds(5)
                }, Ct);

            var executionResult = Assert.Single(execution.ExecutionResults);
            Assert.Equal(1, execution.CandidateAttemptCount);
            Assert.Equal(1, execution.ExecutedAttemptCount);
            Assert.Equal(0, execution.SkippedAttemptCount);
            Assert.Equal(0, execution.FailureCount);
            Assert.Equal(expectedExecutionStatus, executionResult.Status);
            Assert.Equal(expectedErrorCode, executionResult.ErrorCode);
            Assert.Contains(expectedErrorMessageToken, executionResult.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal(1, httpHandler.CallCount);

            Assert.Equal(0, stale.CandidateBatchCount);
            Assert.Empty(stale.MarkedBatchIds);
            Assert.Equal(0, operationId.CandidateCount);
            Assert.Equal(0, operationId.EvidenceAttachedCount);
            Assert.Equal(0, settlement.CandidateCount);
            Assert.Equal(0, settlement.SettledCount);

            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.Prepared));
            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.Sending));
            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.Accepted));
            Assert.Equal(1, await CountAttemptsInStateAsync(con, poolId,
                PayoutSendAttemptStates.AmbiguousRequiresReview));
            Assert.Equal(0, await CountAttemptsInStateAsync(con, poolId, PayoutSendAttemptStates.FailedPreAccept));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.TxId));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.RawHash));
            Assert.Equal(0, await CountConfirmationsAsync(con, poolId, PayoutExternalConfirmationKinds.OperationId));
            Assert.Equal(0, await CountPoolRowsAsync(con, "payments", poolId));
            Assert.Equal(0, await CountNegativeBalanceChangesAsync(con, poolId));
            Assert.Equal(balanceBefore, await SumBalancesAsync(con, poolId));
            Assert.Equal(0, await CountIntentsInStateAsync(con, poolId, PayoutIntentStates.Settled));

            var errorCode = await GetOnlyAttemptErrorCodeAsync(con, poolId);
            var errorMessage = await GetOnlyAttemptErrorMessageAsync(con, poolId);
            Assert.Equal(expectedErrorCode, errorCode);
            Assert.Contains(expectedErrorMessageToken, errorMessage, StringComparison.Ordinal);
            AssertDoesNotLeak(errorCode, endpoint, username, password, walletName, "Authorization", "Basic",
                "userinfo", "SECRET_ENDPOINT_TOKEN", "SECRET_USERNAME_TOKEN", "SECRET_PASSWORD_TOKEN",
                "SECRET_WALLET_TOKEN");
            AssertDoesNotLeak(errorMessage, endpoint, username, password, walletName, "Authorization", "Basic",
                "userinfo", "SECRET_ENDPOINT_TOKEN", "SECRET_USERNAME_TOKEN", "SECRET_PASSWORD_TOKEN",
                "SECRET_WALLET_TOKEN");
        });
    }

    private Pipeline NewPipeline(
        string poolId,
        FakeBitcoinRpcHttpHandler httpHandler,
        string endpoint = "http://127.0.0.1:18443",
        string username = "rpc-user",
        string password = "rpc-password",
        string walletName = "wallet-a",
        string walletPassphrase = "",
        int walletUnlockSeconds = 0,
        bool lockWalletAfterSend = true)
    {
        var resolver = new DictionaryProfileResolver(ReadyBitcoinTxIdProfile());
        var connectionFactory = new PgConnectionFactory(GetConnectionString());
        var senderRegistry = BitcoinRpcPayoutSenderRegistryBuilder.Build(
            new PayoutProcessorConfig
            {
                Enabled = true,
                Mode = PayoutProcessorMode.DbMutating,
                FakeAdaptersOnly = false,
                BitcoinRpcAdapters = new[]
                {
                    new PayoutProcessorBitcoinRpcAdapterConfig
                    {
                        Enabled = true,
                        PoolId = poolId,
                        Coin = Coin,
                        Endpoint = endpoint,
                        Username = username,
                        Password = password,
                        WalletName = walletName,
                        WalletPassphrase = walletPassphrase,
                        WalletUnlockSeconds = walletUnlockSeconds,
                        LockWalletAfterSend = lockWalletAfterSend,
                        RequestTimeoutSeconds = 30,
                        AllowSendMany = true,
                        AllowSendToAddress = true
                    }
                }
            },
            new PayoutProcessorClusterConfig
            {
                Pools = new[]
                {
                    new PayoutProcessorClusterPoolConfig
                    {
                        Id = poolId,
                        Enabled = true,
                        Coin = PoolCoin,
                        PaymentProcessing = new PayoutProcessorClusterPaymentProcessingConfig
                        {
                            Enabled = true,
                            Engine = "intent",
                            MinimumPayment = 1m
                        }
                    }
                }
            },
            new BitcoinRpcPayoutSenderRegistrationMaterializer(resolver,
                new FakeBitcoinJsonRpcHttpClientProvider(poolId, Coin, new HttpClient(httpHandler))));

        return new Pipeline(
            new DbPayoutReservationRunner(connectionFactory,
                new PayoutReservationService(payoutIntentRepo, payoutReservationRepo)),
            new DbPayoutPlanningRunner(connectionFactory, payoutIntentRepo,
                new PayoutSendAttemptPlannerService(payoutIntentRepo), resolver),
            new DbPayoutExecutionRunner(connectionFactory, payoutIntentRepo,
                new PayoutSendExecutorService(connectionFactory, payoutIntentRepo, resolver), resolver,
                senderRegistry),
            new DbPayoutStaleSendReconciliationRunner(connectionFactory,
                new PayoutStaleSendReconciliationService(payoutIntentRepo)),
            new DbPayoutOperationIdReconciliationRunner(connectionFactory, payoutIntentRepo, resolver,
                new PayoutOperationStatusProviderRegistry(
                    Array.Empty<PayoutOperationStatusProviderRegistration>()),
                new PayoutOperationIdReconciliationService(connectionFactory, payoutIntentRepo, resolver)),
            new DbPayoutSettlementRunner(connectionFactory, payoutSettlementRepo,
                new PayoutSettlementService(payoutSettlementRepo, resolver)));
    }

    private static PayoutStaleSendReconciliationRunnerRequest NewStaleRequest(string poolId, DateTime olderThan,
        DateTime updated)
    {
        return new PayoutStaleSendReconciliationRunnerRequest
        {
            PoolId = poolId,
            OlderThan = olderThan,
            Updated = updated,
            Limit = 10,
            ErrorCode = "test_stale_guard",
            ErrorMessage = "test stale guard"
        };
    }

    private static PayoutProfile ReadyBitcoinTxIdProfile()
    {
        return new PayoutProfile
        {
            CoinKey = Coin,
            CoinFamily = PayoutProfileConstants.Families.Bitcoin,
            AdapterId = PayoutProfileConstants.AdapterIds.BitcoinRpc,
            SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
            SendMethod = PayoutProfileConstants.SendMethods.SendMany,
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
            SupportsTransparentTxId = true,
            AllowsBatchMultiRecipient = true,
            RequiresWalletDaemon = true,
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

    private static Task<decimal> SumPaymentsAsync(NpgsqlConnection con, string poolId)
    {
        return con.QuerySingleAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM payments WHERE poolid = @poolid",
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

    private static Task<string> GetConfirmationValueAsync(NpgsqlConnection con, string poolId, string kind)
    {
        return con.QuerySingleAsync<string>(@"SELECT value FROM payout_external_confirmations
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

    private static Task<string> GetOnlyAttemptErrorCodeAsync(NpgsqlConnection con, string poolId)
    {
        return con.QuerySingleOrDefaultAsync<string>(@"SELECT errorcode FROM payout_send_attempts
            WHERE poolid = @poolid",
            new { poolid = poolId });
    }

    private static Task<string> GetOnlyAttemptErrorMessageAsync(NpgsqlConnection con, string poolId)
    {
        return con.QuerySingleOrDefaultAsync<string>(@"SELECT errormessage FROM payout_send_attempts
            WHERE poolid = @poolid",
            new { poolid = poolId });
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }

    private static void AssertDoesNotLeak(string value, params string[] secrets)
    {
        Assert.All(secrets.Where(x => !string.IsNullOrWhiteSpace(x)), secret =>
            Assert.DoesNotContain(secret, value ?? string.Empty, StringComparison.Ordinal));
    }

    private sealed record Pipeline(
        DbPayoutReservationRunner ReservationRunner,
        DbPayoutPlanningRunner PlanningRunner,
        DbPayoutExecutionRunner ExecutionRunner,
        DbPayoutStaleSendReconciliationRunner StaleRunner,
        DbPayoutOperationIdReconciliationRunner OperationIdRunner,
        DbPayoutSettlementRunner SettlementRunner);

    private sealed class DictionaryProfileResolver : IPayoutProfileResolver
    {
        public DictionaryProfileResolver(params PayoutProfile[] profiles)
        {
            this.profiles = profiles.ToDictionary(x => x.CoinKey, StringComparer.OrdinalIgnoreCase);
        }

        private readonly Dictionary<string, PayoutProfile> profiles;

        public PayoutProfileResolution Resolve(string coinKey)
        {
            return profiles.TryGetValue(coinKey, out var profile)
                ? PayoutProfileResolution.Resolved(profile)
                : PayoutProfileResolution.Unsupported("coin not configured");
        }
    }

    private sealed class FakeBitcoinJsonRpcHttpClientProvider : IBitcoinJsonRpcHttpClientProvider
    {
        public FakeBitcoinJsonRpcHttpClientProvider(string poolId, string coin, HttpClient client)
        {
            this.poolId = poolId;
            this.coin = coin;
            this.client = client;
        }

        private readonly string poolId;
        private readonly string coin;
        private readonly HttpClient client;

        public HttpClient GetHttpClient(BitcoinJsonRpcHttpClientRoute route)
        {
            return string.Equals(route.PoolId, poolId, StringComparison.Ordinal) &&
                   string.Equals(route.Coin, coin, StringComparison.Ordinal)
                ? client
                : null;
        }
    }

    private sealed class FakeBitcoinRpcHttpHandler : HttpMessageHandler
    {
        public FakeBitcoinRpcHttpHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            responses.Enqueue(new FakeHttpResponse(responseBody, statusCode));
        }

        public FakeBitcoinRpcHttpHandler(IEnumerable<object> responses)
        {
            foreach(var response in responses)
                this.responses.Enqueue(response is string body
                    ? new FakeHttpResponse(body, HttpStatusCode.OK)
                    : response);
        }

        public FakeBitcoinRpcHttpHandler(Exception exception)
        {
            responses.Enqueue(exception);
        }

        private readonly Queue<object> responses = new();
        public int CallCount { get; private set; }
        public HttpMethod LastMethod { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;
        public List<string> RequestBodies { get; } = new();
        public List<string> Methods { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastMethod = request.Method;
            LastRequestBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(LastRequestBody);
            Methods.Add(ReadMethod(LastRequestBody));

            var next = responses.Count > 0
                ? responses.Dequeue()
                : new FakeHttpResponse("{\"result\":\"txid-default\",\"error\":null,\"id\":\"1\"}",
                    HttpStatusCode.OK);

            if(next is Exception exception)
                throw exception;

            var response = (FakeHttpResponse)next;

            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body)
            };
        }

        private static string ReadMethod(string requestBody)
        {
            if(string.IsNullOrWhiteSpace(requestBody))
                return string.Empty;

            using var document = JsonDocument.Parse(requestBody);
            return document.RootElement.GetProperty("method").GetString();
        }

        private sealed record FakeHttpResponse(string Body, HttpStatusCode StatusCode);
    }
}
