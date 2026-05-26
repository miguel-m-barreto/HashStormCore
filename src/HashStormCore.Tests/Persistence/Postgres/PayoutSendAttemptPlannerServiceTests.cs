using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace HashStormCore.Tests.Persistence.Postgres;

public class PayoutSendAttemptPlannerServiceTests : PostgresIntegrationTestBase
{
    private const string Coin = "testcoin";
    private const string CoinFamily = "testfamily";
    private const string Handler = "test-handler";
    private const string Method = "test-send";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PayoutIntentRepository payoutIntentRepo = new();
    private readonly PayoutSendAttemptPlannerService service;

    public PayoutSendAttemptPlannerServiceTests()
    {
        service = new PayoutSendAttemptPlannerService(payoutIntentRepo);
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_PlansBatchMultiRecipientAttempt()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_batch"), PayoutSendShapes.BatchMultiRecipient,
                ("addr-b", 2m), ("addr-a", 1m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.BatchMultiRecipient, maxRecipientsPerAttempt: 0), Ct);

            var attempt = Assert.Single(result.Attempts);
            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(PayoutSendAttemptStates.Prepared, attempt.State);
            Assert.Equal(1, attempt.AttemptNo);
            Assert.Equal(2, attempt.RecipientCount);
            Assert.Equal(3m, attempt.AmountSnapshot);
            Assert.Equal(PayoutBatchStates.Reserved, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(2, await CountMappingsAsync(con, tx, batch.Id, PayoutAttemptIntentStates.Active));
            Assert.Equal(new[] { "addr-a", "addr-b" }, await GetAttemptAddressesAsync(con, tx, attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_PlansAsyncOperationAttempt()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_async"), PayoutSendShapes.AsyncOperation,
                ("addr-a", 1m), ("addr-b", 2m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AsyncOperation, maxRecipientsPerAttempt: 0), Ct);

            var attempt = Assert.Single(result.Attempts);
            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(PayoutSendAttemptStates.Prepared, attempt.State);
            Assert.Equal(2, attempt.RecipientCount);
            Assert.Equal(2, await CountMappingsAsync(con, tx, batch.Id, PayoutAttemptIntentStates.Active));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_PlansPerAddressAttempts()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_per_address"), PayoutSendShapes.PerAddress,
                ("addr-b", 2m), ("addr-a", 1m), ("addr-c", 3m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.PerAddress, maxRecipientsPerAttempt: 0), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(3, result.Attempts.Count);
            Assert.Equal(new[] { 1, 2, 3 }, result.Attempts.Select(x => x.AttemptNo).ToArray());
            Assert.All(result.Attempts, attempt =>
            {
                Assert.Equal(PayoutSendAttemptStates.Prepared, attempt.State);
                Assert.Equal(1, attempt.RecipientCount);
                Assert.Single(attempt.AttemptIntents);
            });

            Assert.Equal(new[] { "addr-a" }, await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { "addr-b" }, await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
            Assert.Equal(new[] { "addr-c" }, await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(2).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_PlansAddressGroupChunks()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_group"), PayoutSendShapes.AddressGroup,
                ("addr-e", 5m), ("addr-a", 1m), ("addr-c", 3m), ("addr-b", 2m), ("addr-d", 4m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt: 2), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(new[] { 2, 2, 1 }, result.Attempts.Select(x => x.RecipientCount).ToArray());
            Assert.Equal(new[] { "addr-a", "addr-b" }, await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { "addr-c", "addr-d" }, await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
            Assert.Equal(new[] { "addr-e" }, await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(2).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_ConcealPolicySplitsExplicitPaymentIds()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var paymentId = new string('a', 64);
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_conceal_paymentid"),
                PayoutSendShapes.AddressGroup,
                ("addr-b", 2m), ("addr-a", 1m), ($"addr-c.{paymentId}", 3m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt: 15) with
                {
                    AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.ConcealPaymentIdAware,
                    IntegratedAddressPrefixes = new[] { 19ul }
                }, Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(new[] { 2, 1 }, result.Attempts.Select(x => x.RecipientCount).ToArray());
            Assert.Equal(new[] { "addr-a", "addr-b" }, await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { $"addr-c.{paymentId}" }, await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_ConcealPolicyTreatsInvalidPaymentIdSuffixAsSimple()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_conceal_invalid_pid"),
                PayoutSendShapes.AddressGroup,
                ("addr-b.bad", 2m), ("addr-a", 1m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt: 15) with
                {
                    AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.ConcealPaymentIdAware,
                    IntegratedAddressPrefixes = new[] { 19ul }
                }, Ct);

            var attempt = Assert.Single(result.Attempts);
            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(2, attempt.RecipientCount);
            Assert.Equal(new[] { "addr-a", "addr-b.bad" }, await GetAttemptAddressesAsync(con, tx, attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_ConcealPolicySplitsIntegratedAddresses()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var integratedAddress =
                "4BrL51JCc9NGQ71kWhnYoDRffsDZy7m1HUU7MRU4nUMXAHNFBEJhkTZV9HdaL4gfuNBxLPc3BeMkLGaPbF5vWtANQsGwTGg55Kq4p3ENE7";
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_conceal_integrated"),
                PayoutSendShapes.AddressGroup,
                ("addr-a", 1m), ("addr-b", 2m), (integratedAddress, 3m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt: 15) with
                {
                    AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.ConcealPaymentIdAware,
                    IntegratedAddressPrefixes = new[] { 19ul }
                }, Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(new[] { 2, 1 }, result.Attempts.Select(x => x.RecipientCount).ToArray());
            Assert.Equal(new[] { "addr-a", "addr-b" }, await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { integratedAddress }, await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_ConcealPolicyUsesPayloadLengthWhenStandardAndIntegratedSharePrefix()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            const ulong concealPrefix = 31444;
            var standardAddress = CreateCryptoNoteAddress(concealPrefix, payloadLength: 68);
            var integratedAddress = CreateCryptoNoteAddress(concealPrefix, payloadLength: 76);
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_conceal_same_prefix"),
                PayoutSendShapes.AddressGroup,
                ("addr-a", 1m), (standardAddress, 2m), (integratedAddress, 3m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt: 15) with
                {
                    AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.ConcealPaymentIdAware,
                    IntegratedAddressPrefixes = new[] { concealPrefix }
                }, Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(new[] { 2, 1 }, result.Attempts.Select(x => x.RecipientCount).ToArray());
            Assert.Equal(new[] { "addr-a", standardAddress }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { integratedAddress },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_ConcealPolicyDoesNotTreatUnknownPayloadLengthAsIntegrated()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            const ulong concealPrefix = 31444;
            var unknownLengthAddress = CreateCryptoNoteAddress(concealPrefix, payloadLength: 70);
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_conceal_unknown_payload"),
                PayoutSendShapes.AddressGroup,
                ("addr-a", 1m), (unknownLengthAddress, 2m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt: 15) with
                {
                    AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.ConcealPaymentIdAware,
                    IntegratedAddressPrefixes = new[] { concealPrefix }
                }, Ct);

            var attempt = Assert.Single(result.Attempts);
            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(2, attempt.RecipientCount);
            Assert.Equal(new[] { "addr-a", unknownLengthAddress }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                await GetAttemptAddressesAsync(con, tx, attempt.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_CryptonotePolicySplitsPaymentIdAndIntegratedIntents()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            const ulong integratedPrefix = 19;
            var paymentId = new string('b', 64);
            var standardAddress = CreateCryptoNoteAddress(integratedPrefix, payloadLength: 68);
            var integratedAddress = CreateCryptoNoteAddress(integratedPrefix, payloadLength: 76);
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_cn_policy"),
                PayoutSendShapes.AddressGroup,
                ("addr-a", 1m), ("addr-b.bad", 2m), (standardAddress, 3m),
                ($"addr-c.{paymentId}", 4m), (integratedAddress, 5m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt: 15) with
                {
                    AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.CryptonotePaymentIdAware,
                    IntegratedAddressPrefixes = new[] { integratedPrefix }
                }, Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(new[] { 3, 1, 1 }, result.Attempts.Select(x => x.RecipientCount).ToArray());
            Assert.Equal(new[] { "addr-a", "addr-b.bad", standardAddress }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { $"addr-c.{paymentId}" },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
            Assert.Equal(new[] { integratedAddress },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(2).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_ZanoPolicySplitsPaymentIdAndIntegratedIntents()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            const ulong integratedPrefix = 13944;
            var paymentId = new string('c', 64);
            var standardAddress = CreateCryptoNoteAddress(integratedPrefix, payloadLength: 68);
            var integratedAddress = CreateCryptoNoteAddress(integratedPrefix, payloadLength: 76);
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_zano_policy"),
                PayoutSendShapes.AddressGroup,
                ("addr-a", 1m), ("addr-b.bad", 2m), (standardAddress, 3m),
                ($"addr-c.{paymentId}", 4m), (integratedAddress, 5m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt: 256) with
                {
                    AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.ZanoPaymentIdAware,
                    IntegratedAddressPrefixes = new[] { integratedPrefix }
                }, Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(new[] { 3, 1, 1 }, result.Attempts.Select(x => x.RecipientCount).ToArray());
            Assert.Equal(new[] { "addr-a", "addr-b.bad", standardAddress }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { $"addr-c.{paymentId}" },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
            Assert.Equal(new[] { integratedAddress },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(2).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_ZanoPolicyClassifiesAddressV2IntegratedPrefixByPayloadLength()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            const ulong v2IntegratedPrefix = 14072;
            var integratedAddress = CreateCryptoNoteAddress(v2IntegratedPrefix, payloadLength: 76);
            var standardLengthAddress = CreateCryptoNoteAddress(v2IntegratedPrefix, payloadLength: 68);
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_zano_v2_integrated"),
                PayoutSendShapes.AddressGroup,
                ("addr-a", 1m), (integratedAddress, 2m), (standardLengthAddress, 3m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt: 256) with
                {
                    AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.ZanoPaymentIdAware,
                    IntegratedAddressPrefixes = new[] { 13944ul, v2IntegratedPrefix, 35401ul }
                }, Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(new[] { 2, 1 }, result.Attempts.Select(x => x.RecipientCount).ToArray());
            Assert.Equal(
                new[] { "addr-a", standardLengthAddress }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { integratedAddress },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_ZanoPolicyClassifiesAuditableIntegratedPrefixByPayloadLength()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            const ulong auditableIntegratedPrefix = 35401;
            var integratedAddress = CreateCryptoNoteAddress(auditableIntegratedPrefix, payloadLength: 76);
            var standardLengthAddress = CreateCryptoNoteAddress(auditableIntegratedPrefix, payloadLength: 68);
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_zano_auditable_integrated"),
                PayoutSendShapes.AddressGroup,
                ("addr-a", 1m), (integratedAddress, 2m), (standardLengthAddress, 3m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt: 256) with
                {
                    AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.ZanoPaymentIdAware,
                    IntegratedAddressPrefixes = new[] { 13944ul, 14072ul, auditableIntegratedPrefix }
                }, Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(new[] { 2, 1 }, result.Attempts.Select(x => x.RecipientCount).ToArray());
            Assert.Equal(
                new[] { "addr-a", standardLengthAddress }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { integratedAddress },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_ExistingAttemptsReturnNoOp()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_existing"), PayoutSendShapes.PerAddress,
                ("addr-a", 1m), ("addr-b", 2m));

            var first = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.PerAddress, maxRecipientsPerAttempt: 0), Ct);
            var attemptCount = await CountAttemptsAsync(con, tx, batch.Id);

            var second = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.PerAddress, maxRecipientsPerAttempt: 0), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, first.Status);
            Assert.Equal(PayoutSendAttemptPlanningStatus.AttemptsAlreadyExist, second.Status);
            Assert.Empty(second.Attempts);
            Assert.Equal(attemptCount, await CountAttemptsAsync(con, tx, batch.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_ReturnsBatchPreconditionStatuses()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var missing = await service.CreateSendAttemptsAsync(con, tx, new CreatePayoutSendAttemptsRequest
            {
                BatchId = 999999999,
                PoolId = NewPoolId("planner_missing"),
                Coin = Coin,
                SendShape = PayoutSendShapes.PerAddress,
                Method = Method,
                Created = UtcNow()
            }, Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.BatchNotFound, missing.Status);

            var nonReservedBatch = await CreateBatchAsync(con, tx, NewPoolId("planner_not_reserved"),
                PayoutSendShapes.PerAddress, ("addr-a", 1m));
            await con.ExecuteAsync("UPDATE payout_batches SET state = @state WHERE id = @batchid",
                new { state = PayoutBatchStates.Sending, batchid = nonReservedBatch.Id }, tx);

            var notReserved = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(nonReservedBatch, PayoutSendShapes.PerAddress, maxRecipientsPerAttempt: 0), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.BatchNotReserved, notReserved.Status);

            var noReservedIntentsBatch = await CreateBatchAsync(con, tx, NewPoolId("planner_no_intents"),
                PayoutSendShapes.PerAddress, ("addr-a", 1m));
            await con.ExecuteAsync("UPDATE payout_intents SET state = @state WHERE batchid = @batchid",
                new { state = PayoutIntentStates.Cancelled, batchid = noReservedIntentsBatch.Id }, tx);

            var noReservedIntents = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(noReservedIntentsBatch, PayoutSendShapes.PerAddress, maxRecipientsPerAttempt: 0), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.NoReservedIntents, noReservedIntents.Status);
            Assert.Equal(0, await CountAttemptsAsync(con, tx, noReservedIntentsBatch.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_SendShapeMismatchThrows()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_shape_mismatch"), PayoutSendShapes.PerAddress,
                ("addr-a", 1m));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateSendAttemptsAsync(con, tx,
                    NewRequest(batch, PayoutSendShapes.BatchMultiRecipient, maxRecipientsPerAttempt: 0), Ct));

            Assert.Equal(0, await CountAttemptsAsync(con, tx, batch.Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_RequestHashIsDeterministicAndSanitized()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_hash"), PayoutSendShapes.BatchMultiRecipient,
                created, ("addr-b", 2m), ("addr-a", 1m));
            var request = NewRequest(batch, PayoutSendShapes.BatchMultiRecipient, maxRecipientsPerAttempt: 0, created: created);

            var result = await service.CreateSendAttemptsAsync(con, tx, request, Ct);

            var attempt = Assert.Single(result.Attempts);
            Assert.Equal(CreateExpectedRequestHash(request, attempt.AttemptNo, batch.Intents), attempt.RequestHash);
            Assert.Equal("batch_multi_recipient:test-send:recipients=2:amount=3", attempt.RequestSummary);
            Assert.DoesNotContain("addr-a", attempt.RequestSummary);
            Assert.DoesNotContain("addr-b", attempt.RequestSummary);
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_DoesNotMutateAccountingOrBalances()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var poolId = NewPoolId("planner_no_mutation");
            var now = UtcNow();
            await InsertBalanceAsync(con, tx, poolId, "addr-balance", 10m, now);
            var batch = await CreateBatchAsync(con, tx, poolId, PayoutSendShapes.BatchMultiRecipient, ("addr-a", 1m));

            var balanceBefore = await SumBalancesAsync(con, tx, poolId);
            var paymentsBefore = await CountPoolRowsAsync(con, tx, "payments", poolId);
            var balanceChangesBefore = await CountPoolRowsAsync(con, tx, "balance_changes", poolId);

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewRequest(batch, PayoutSendShapes.BatchMultiRecipient, maxRecipientsPerAttempt: 0), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(balanceBefore, await SumBalancesAsync(con, tx, poolId));
            Assert.Equal(paymentsBefore, await CountPoolRowsAsync(con, tx, "payments", poolId));
            Assert.Equal(balanceChangesBefore, await CountPoolRowsAsync(con, tx, "balance_changes", poolId));
            Assert.Equal(PayoutBatchStates.Reserved, await GetBatchStateAsync(con, tx, batch.Id));
            Assert.Equal(batch.Intents.Length, await CountIntentsAsync(con, tx, batch.Id, PayoutIntentStates.Reserved));
            Assert.Equal(result.Attempts.Count, await CountAttemptsAsync(con, tx, batch.Id, PayoutSendAttemptStates.Prepared));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_RejectsInvalidRequestsAndRequiresTransaction()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_invalid"), PayoutSendShapes.PerAddress,
                ("addr-a", 1m));
            var valid = NewRequest(batch, PayoutSendShapes.PerAddress, maxRecipientsPerAttempt: 0);

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.CreateSendAttemptsAsync(null, tx, valid, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.CreateSendAttemptsAsync(con, null, valid, Ct));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.CreateSendAttemptsAsync(con, tx, null, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.CreateSendAttemptsAsync(con, tx, valid with { BatchId = 0 }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateSendAttemptsAsync(con, tx, valid with { PoolId = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateSendAttemptsAsync(con, tx, valid with { Coin = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateSendAttemptsAsync(con, tx, valid with { SendShape = "unknown" }, Ct));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateSendAttemptsAsync(con, tx, valid with { Method = " " }, Ct));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.CreateSendAttemptsAsync(con, tx, valid with
                {
                    SendShape = PayoutSendShapes.AddressGroup,
                    MaxRecipientsPerAttempt = 0
                }, Ct));
        });
    }

    private Task<PayoutBatch> CreateBatchAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId, string sendShape,
        params (string address, decimal amount)[] intents)
    {
        return CreateBatchAsync(con, tx, poolId, sendShape, UtcNow(), intents);
    }

    private Task<PayoutBatch> CreateBatchAsync(NpgsqlConnection con, NpgsqlTransaction tx, string poolId, string sendShape,
        DateTime created, params (string address, decimal amount)[] intents)
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
            SendShape = sendShape,
            RecipientSetHash = $"recipient-set-{Guid.NewGuid():N}",
            MinimumAmount = 0m,
            ReservedAmountSnapshot = intents.Sum(x => x.amount),
            IntentCountSnapshot = intents.Length,
            Created = created
        }, intentRequests, Ct);
    }

    private static CreatePayoutSendAttemptsRequest NewRequest(PayoutBatch batch, string sendShape, int maxRecipientsPerAttempt,
        DateTime? created = null)
    {
        return new CreatePayoutSendAttemptsRequest
        {
            BatchId = batch.Id,
            PoolId = batch.PoolId,
            Coin = batch.Coin,
            SendShape = sendShape,
            Method = Method,
            MaxRecipientsPerAttempt = maxRecipientsPerAttempt,
            Created = created ?? UtcNow()
        };
    }

    private static string CreateExpectedRequestHash(CreatePayoutSendAttemptsRequest request, int attemptNo,
        IReadOnlyCollection<PayoutIntent> intents)
    {
        var builder = new StringBuilder();
        builder.AppendLine("HashStormCore:payout-send-attempt:v1");
        builder.Append("batchid=").AppendLine(request.BatchId.ToString(CultureInfo.InvariantCulture));
        builder.Append("poolid=").AppendLine(request.PoolId);
        builder.Append("coin=").AppendLine(request.Coin);
        builder.Append("sendshape=").AppendLine(request.SendShape);
        builder.Append("method=").AppendLine(request.Method);
        builder.Append("attemptindex=").AppendLine(attemptNo.ToString(CultureInfo.InvariantCulture));

        foreach(var intent in intents.OrderBy(x => x.Address, StringComparer.Ordinal).ThenBy(x => x.Id))
        {
            builder.Append("intentid=").Append(intent.Id.ToString(CultureInfo.InvariantCulture))
                .Append('\t').Append("address=").Append(intent.Address)
                .Append('\t').Append("amount=").Append(FormatDecimal(intent.Amount))
                .AppendLine();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Task<string> GetBatchStateAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId)
    {
        return con.QuerySingleAsync<string>("SELECT state FROM payout_batches WHERE id = @batchid",
            new { batchid = batchId }, tx);
    }

    private static Task<int> CountAttemptsAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId)
    {
        return con.QuerySingleAsync<int>("SELECT COUNT(*) FROM payout_send_attempts WHERE batchid = @batchid",
            new { batchid = batchId }, tx);
    }

    private static Task<int> CountAttemptsAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId, string state)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_send_attempts
            WHERE batchid = @batchid AND state = @state",
            new { batchid = batchId, state }, tx);
    }

    private static Task<int> CountIntentsAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId, string state)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_intents
            WHERE batchid = @batchid AND state = @state",
            new { batchid = batchId, state }, tx);
    }

    private static Task<int> CountMappingsAsync(NpgsqlConnection con, NpgsqlTransaction tx, long batchId, string state)
    {
        return con.QuerySingleAsync<int>(@"SELECT COUNT(*) FROM payout_attempt_intents
            WHERE batchid = @batchid AND state = @state",
            new { batchid = batchId, state }, tx);
    }

    private static async Task<string[]> GetAttemptAddressesAsync(NpgsqlConnection con, NpgsqlTransaction tx, long attemptId)
    {
        const string query = @"SELECT pi.address
            FROM payout_attempt_intents pai
            JOIN payout_intents pi ON pi.id = pai.intentid
            WHERE pai.attemptid = @attemptid
            ORDER BY pi.address";

        return (await con.QueryAsync<string>(query, new { attemptid = attemptId }, tx)).ToArray();
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

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    private static string CreateCryptoNoteAddress(ulong prefix, int payloadLength)
    {
        var prefixBytes = EncodeVarInt(prefix);
        var decoded = new byte[prefixBytes.Length + payloadLength];
        Buffer.BlockCopy(prefixBytes, 0, decoded, 0, prefixBytes.Length);

        for(var i = 0; i < payloadLength; i++)
            decoded[prefixBytes.Length + i] = (byte) (i + 1);

        return EncodeCryptoNoteBase58(decoded);
    }

    private static byte[] EncodeVarInt(ulong value)
    {
        var bytes = new List<byte>();

        while(value >= 0x80)
        {
            bytes.Add((byte) ((value & 0x7f) | 0x80));
            value >>= 7;
        }

        bytes.Add((byte) value);
        return bytes.ToArray();
    }

    private static string EncodeCryptoNoteBase58(byte[] bytes)
    {
        var builder = new StringBuilder();
        var offset = 0;

        while(bytes.Length - offset >= 8)
        {
            builder.Append(EncodeCryptoNoteBase58Block(bytes.AsSpan(offset, 8), encodedSize: 11));
            offset += 8;
        }

        var remaining = bytes.Length - offset;
        if(remaining > 0)
            builder.Append(EncodeCryptoNoteBase58Block(bytes.AsSpan(offset, remaining), EncodedBlockSize(remaining)));

        return builder.ToString();
    }

    private static string EncodeCryptoNoteBase58Block(ReadOnlySpan<byte> bytes, int encodedSize)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        ulong value = 0;

        foreach(var b in bytes)
            value = (value << 8) | b;

        var chars = new char[encodedSize];
        Array.Fill(chars, alphabet[0]);

        for(var i = encodedSize - 1; i >= 0 && value > 0; i--)
        {
            chars[i] = alphabet[(int) (value % 58)];
            value /= 58;
        }

        return new string(chars);
    }

    private static int EncodedBlockSize(int decodedSize)
    {
        return decodedSize switch
        {
            1 => 2,
            2 => 3,
            3 => 5,
            4 => 6,
            5 => 7,
            6 => 9,
            7 => 10,
            8 => 11,
            _ => throw new ArgumentOutOfRangeException(nameof(decodedSize))
        };
    }

    private static DateTime UtcNow()
    {
        return DateTime.UtcNow;
    }
}
