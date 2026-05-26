using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Numerics;
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
    public Task CreateSendAttemptsAsync_AlephiumPolicyGroupsSameGroupAddressesTogether()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            // Official P2PKH fixtures — both expected group 1 with AddressGroupCount=4
            const string group1Address1 = "1H7CmpbvGJwgyLzR91wzSJJSkiBC92WDPTWny4gmhQJQc";
            const string group1Address2 = "1C2RAVWSuaXw8xtUxqVERR7ChKBE1XgscNFw73NSHE1v3";
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_same_group"),
                PayoutSendShapes.AddressGroup, (group1Address1, 1m), (group1Address2, 2m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewAlephiumRequest(batch, maxRecipientsPerAttempt: 64), Ct);

            var attempt = Assert.Single(result.Attempts);
            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(2, attempt.RecipientCount);
            var addresses = await GetAttemptAddressesAsync(con, tx, attempt.Id);
            Assert.Contains(group1Address1, addresses);
            Assert.Contains(group1Address2, addresses);
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicySeparatesDifferentGroupAddresses()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            // Official P2PKH fixtures across groups 0, 1, 3 with AddressGroupCount=4.
            const string group0Address = "1DkrQMni2h8KYpvY8t7dECshL66gwnxiR5uD2Udxps6og";
            const string group1Address = "1H7CmpbvGJwgyLzR91wzSJJSkiBC92WDPTWny4gmhQJQc";
            const string group3Address = "131R8ufDhcsu6SRztR9D3m8GUzkWFUPfT78aQ6jgtgzob";
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_diff_groups"),
                PayoutSendShapes.AddressGroup,
                (group0Address, 1m), (group1Address, 2m), (group3Address, 3m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewAlephiumRequest(batch, maxRecipientsPerAttempt: 64), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            // One attempt per occupied group (groups 0, 1, 3 in order)
            Assert.Equal(3, result.Attempts.Count);
            Assert.All(result.Attempts, a => Assert.Equal(1, a.RecipientCount));
            Assert.Equal(new[] { group0Address },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { group1Address },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
            Assert.Equal(new[] { group3Address },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(2).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyAllFourOfficialP2PKHFixturesClassifiedCorrectly()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            // All four official P2PKH fixtures using AddressGroupCount=4:
            // 1C2R... -> 1, 1H7... -> 1, 1Dkr... -> 0, 131... -> 3.
            const string group0A = "1DkrQMni2h8KYpvY8t7dECshL66gwnxiR5uD2Udxps6og";
            const string group1A = "1C2RAVWSuaXw8xtUxqVERR7ChKBE1XgscNFw73NSHE1v3";
            const string group1B = "1H7CmpbvGJwgyLzR91wzSJJSkiBC92WDPTWny4gmhQJQc";
            const string group3A = "131R8ufDhcsu6SRztR9D3m8GUzkWFUPfT78aQ6jgtgzob";
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_all_fixtures"),
                PayoutSendShapes.AddressGroup,
                (group0A, 1m), (group1A, 2m), (group1B, 3m), (group3A, 4m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewAlephiumRequest(batch, maxRecipientsPerAttempt: 64), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            // Groups 0 (1 addr), 1 (2 addrs), 3 (1 addr) in group order.
            Assert.Equal(3, result.Attempts.Count);
            Assert.Equal(1, result.Attempts.ElementAt(0).RecipientCount);
            Assert.Equal(2, result.Attempts.ElementAt(1).RecipientCount);
            Assert.Equal(1, result.Attempts.ElementAt(2).RecipientCount);
            Assert.Equal(new[] { group0A },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { group1A, group1B }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
            Assert.Equal(new[] { group3A },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(2).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyP2SHAddressClassifiesByScriptHint()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var scriptHash = CreateAlephiumHash(0x10);
            var p2shAddress = CreateAlephiumLockupScriptAddress(2, scriptHash);

            await AssertAlephiumAddressPlansInGroupAsync(con, tx, p2shAddress, expectedGroup: 0);
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyP2PKExplicitGroupSuffixGroupsWithSameGroupP2PKH()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            // P2PK address with explicit group-0 suffix alongside a P2PKH address in group 0.
            const string p2pkGroup0 = "3ccJ8aEBYKBPJKuk6b9yZ1W1oFDYPesa3qQeM8v9jhaJtbSaueJ3L:0";
            const string p2pkhGroup0 = "1DkrQMni2h8KYpvY8t7dECshL66gwnxiR5uD2Udxps6og";
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_p2pk_suffix"),
                PayoutSendShapes.AddressGroup, (p2pkGroup0, 1m), (p2pkhGroup0, 2m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewAlephiumRequest(batch, maxRecipientsPerAttempt: 64), Ct);

            var attempt = Assert.Single(result.Attempts);
            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(2, attempt.RecipientCount);
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyP2PKExplicitGroupSuffixesClassifyWhenPayloadIsValid()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            const string payload = "3ccJ8aEBYKBPJKuk6b9yZ1W1oFDYPesa3qQeM8v9jhaJtbSaueJ3L";
            var group0 = $"{payload}:0";
            var group1 = $"{payload}:1";
            var group2 = $"{payload}:2";
            var group3 = $"{payload}:3";
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_p2pk_suffixes"),
                PayoutSendShapes.AddressGroup,
                (group0, 1m), (group1, 2m), (group2, 3m), (group3, 4m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewAlephiumRequest(batch, maxRecipientsPerAttempt: 64), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(4, result.Attempts.Count);
            Assert.Equal(new[] { group0 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { group1 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
            Assert.Equal(new[] { group2 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(2).Id));
            Assert.Equal(new[] { group3 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(3).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyP2HMPKExplicitGroupSuffixesClassifyWhenPayloadIsValid()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var payload = CreateAlephiumGroupedAddressPayload(5);
            var group0 = $"{payload}:0";
            var group1 = $"{payload}:1";
            var group2 = $"{payload}:2";
            var group3 = $"{payload}:3";
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_p2hmpk_suffixes"),
                PayoutSendShapes.AddressGroup,
                (group0, 1m), (group1, 2m), (group2, 3m), (group3, 4m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewAlephiumRequest(batch, maxRecipientsPerAttempt: 64), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(4, result.Attempts.Count);
            Assert.Equal(new[] { group0 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { group1 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
            Assert.Equal(new[] { group2 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(2).Id));
            Assert.Equal(new[] { group3 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(3).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyThrowsForInvalidExplicitGroupSuffixPayloads()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var addresses = new[]
            {
                "bad:1",
                "3ccJ8aEBYKBPJKuk6b9yZ1W1oFDYPesa3qQeM8v9jhaJtbSaueJ3L:4",
                "3ccJ8aEBYKBPJKuk6b9yZ1W1oFDYPesa3qQeM8v9jhaJtbSaueJ3L:01",
                "3ccJ8aEBYKBPJKuk6b9yZ1W1oFDYPesa3qQeM8v9jhaJtbSaueJ3L:-1",
                "111111111111111111111111111111111111111:1"
            };

            foreach(var address in addresses)
            {
                var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_bad_suffix"),
                    PayoutSendShapes.AddressGroup, (address, 1m));

                await AssertAlephiumUnclassifiableAsync(con, tx, batch);
            }
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyP2CAddressGroupsByRawLastContractIdByte()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var group0 = CreateAlephiumLockupScriptAddress(3, 0x00);
            var group1 = CreateAlephiumLockupScriptAddress(3, 0x01);
            var group2 = CreateAlephiumLockupScriptAddress(3, 0x02);
            var group3 = CreateAlephiumLockupScriptAddress(3, 0x03);
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_p2c_valid"),
                PayoutSendShapes.AddressGroup,
                (group0, 1m), (group1, 2m), (group2, 3m), (group3, 4m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewAlephiumRequest(batch, maxRecipientsPerAttempt: 64), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(4, result.Attempts.Count);
            Assert.Equal(new[] { group0 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(0).Id));
            Assert.Equal(new[] { group1 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(1).Id));
            Assert.Equal(new[] { group2 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(2).Id));
            Assert.Equal(new[] { group3 },
                await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(3).Id));
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyThrowsForP2CAddressWithInvalidRawGroupByte()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var addresses = new[]
            {
                // Official P2C fixtures with raw group bytes 0xef and 0xa9; they must not be modulo-reduced.
                "22sTaM5xer7h81LzaGA2JiajRwHwECpAv9bBuFUH5rrnr",
                "2AA91hkrsVv14QDZWgxMJXxDDKTRKzZMPyakCVUbZEGoS"
            };

            foreach(var address in addresses)
            {
                var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_p2c_invalid"),
                    PayoutSendShapes.AddressGroup, (address, 1m));

                await AssertAlephiumUnclassifiableAsync(con, tx, batch);
            }
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyThrowsForUnclassifiableAddress()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            const string validAddress = "1H7CmpbvGJwgyLzR91wzSJJSkiBC92WDPTWny4gmhQJQc";
            const string invalidAddress = "not-a-valid-alephium-address";
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_unclassifiable"),
                PayoutSendShapes.AddressGroup, (validAddress, 1m), (invalidAddress, 2m));

            var ex = await AssertAlephiumUnclassifiableAsync(con, tx, batch);

            Assert.Contains("Alephium", ex.Message);
            Assert.Contains("unclassifiable", ex.Message);
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyOfficialP2MPKHFixturesClassifiedCorrectly()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var fixtures = new[]
            {
                ("2jjvDdgGjC6X9HHMCMHohVfvp1uf3LHQrAGWaufR17P7AFwtxodTxSktqKc2urNEtaoUCy5xXpBUwpZ8QM8Q3e5BYCx", 1),
                ("2jjvDdgGjC6X9HHMCMHohVfvp1uf3LHQrAGWaufR17P7AFwtxodTxSktqKc2urNEtaoUCy5xXpBUwpZ8QM8Q3e5BYCy", 1),
                ("X3RMnvb8h3RFrrbBraEouAWU9Ufu4s2WTXUQfLCvDtcmqCWRwkVLc69q2NnwYW2EMwg4QBN2UopkEmYLLLgHP9TQ38FK15RnhhEwguRyY6qCuAoRfyjHRnqYnTvfypPgD7w1ku", 1)
            };

            foreach(var (address, expectedGroup) in fixtures)
                await AssertAlephiumAddressPlansInGroupAsync(con, tx, address, expectedGroup);
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyThrowsForMalformedP2MPKHAddress()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            var malformedAddresses = new[]
            {
                CreateAlephiumP2MPKHAddress(Array.Empty<byte[]>(), threshold: 1),
                CreateAlephiumP2MPKHAddress(new[] { CreateAlephiumHash(0x10) }, threshold: 0),
                CreateAlephiumP2MPKHAddress(new[] { CreateAlephiumHash(0x20) }, threshold: 2),
                CreateMalformedAlephiumP2MPKHAddress(publicKeyHashCount: 1, hashBytesToWrite: 31,
                    threshold: 1, appendTrailing: false),
                CreateAlephiumP2MPKHAddress(new[] { CreateAlephiumHash(0x30) }, threshold: 1,
                    appendTrailing: true)
            };

            foreach(var address in malformedAddresses)
            {
                var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_bad_p2mpkh"),
                    PayoutSendShapes.AddressGroup, (address, 1m));

                await AssertAlephiumUnclassifiableAsync(con, tx, batch);
            }
        });
    }

    [PostgresIntegrationFact]
    public Task CreateSendAttemptsAsync_AlephiumPolicyChunksLargeGroupByMaxRecipientsPerAttempt()
    {
        return WithRollbackAsync(async (con, tx) =>
        {
            // Four group-0 addresses chunked at 2 should produce 2 attempts for group 0.
            const string addr1 = "1DkrQMni2h8KYpvY8t7dECshL66gwnxiR5uD2Udxps6og";
            var addr2 = CreateAlephiumLockupScriptAddress(3, 0x00);
            const string addr3 = "3ccJ8aEBYKBPJKuk6b9yZ1W1oFDYPesa3qQeM8v9jhaJtbSaueJ3L:0";
            const string addr4 = "3ddL9bFECBYCKBQKjb7aZW2pGGEYCesc4oPcbQ9jibcKuebTbfJ4M:0";
            var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_chunks"),
                PayoutSendShapes.AddressGroup,
                (addr1, 1m), (addr2, 2m), (addr3, 3m), (addr4, 4m));

            var result = await service.CreateSendAttemptsAsync(con, tx,
                NewAlephiumRequest(batch, maxRecipientsPerAttempt: 2), Ct);

            Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
            Assert.Equal(2, result.Attempts.Count);
            Assert.Equal(2, result.Attempts.ElementAt(0).RecipientCount);
            Assert.Equal(2, result.Attempts.ElementAt(1).RecipientCount);
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

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.CreateSendAttemptsAsync(con, tx, valid with
                {
                    SendShape = PayoutSendShapes.AddressGroup,
                    MaxRecipientsPerAttempt = 64,
                    AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.AlephiumGroupAware,
                    AddressGroupCount = 0
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

    private static CreatePayoutSendAttemptsRequest NewAlephiumRequest(PayoutBatch batch, int maxRecipientsPerAttempt)
    {
        return NewRequest(batch, PayoutSendShapes.AddressGroup, maxRecipientsPerAttempt) with
        {
            AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.AlephiumGroupAware,
            AddressGroupCount = 4
        };
    }

    private Task<InvalidOperationException> AssertAlephiumUnclassifiableAsync(NpgsqlConnection con, NpgsqlTransaction tx,
        PayoutBatch batch)
    {
        return Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateSendAttemptsAsync(con, tx, NewAlephiumRequest(batch, maxRecipientsPerAttempt: 64), Ct));
    }

    private async Task AssertAlephiumAddressPlansInGroupAsync(NpgsqlConnection con, NpgsqlTransaction tx,
        string targetAddress, int expectedGroup)
    {
        var groupAddresses = Enumerable.Range(0, 4)
            .Select(x => CreateAlephiumLockupScriptAddress(3, (byte) x))
            .ToArray();
        var intents = groupAddresses
            .Select((address, index) => (address, amount: (decimal) (index + 1)))
            .Append((address: targetAddress, amount: 10m))
            .ToArray();
        var batch = await CreateBatchAsync(con, tx, NewPoolId("planner_aleph_expected_group"),
            PayoutSendShapes.AddressGroup, intents);

        var result = await service.CreateSendAttemptsAsync(con, tx,
            NewAlephiumRequest(batch, maxRecipientsPerAttempt: 64), Ct);

        Assert.Equal(PayoutSendAttemptPlanningStatus.Created, result.Status);
        Assert.Equal(4, result.Attempts.Count);

        for(var i = 0; i < result.Attempts.Count; i++)
        {
            var addresses = await GetAttemptAddressesAsync(con, tx, result.Attempts.ElementAt(i).Id);
            if(i == expectedGroup)
                Assert.Contains(targetAddress, addresses);
            else
                Assert.DoesNotContain(targetAddress, addresses);
        }
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

    private static string CreateAlephiumLockupScriptAddress(byte typeByte, byte lastByte)
    {
        var decoded = new byte[33];
        decoded[0] = typeByte;

        for(var i = 1; i < decoded.Length - 1; i++)
            decoded[i] = (byte) i;

        decoded[^1] = lastByte;
        return EncodeStandardBase58(decoded);
    }

    private static string CreateAlephiumLockupScriptAddress(byte typeByte, byte[] hashBytes)
    {
        if(hashBytes.Length != 32)
            throw new ArgumentException("Alephium hash fixtures must be 32 bytes");

        var decoded = new byte[33];
        decoded[0] = typeByte;
        Buffer.BlockCopy(hashBytes, 0, decoded, 1, hashBytes.Length);
        return EncodeStandardBase58(decoded);
    }

    private static string CreateAlephiumGroupedAddressPayload(byte typeByte)
    {
        var decoded = new byte[39];
        decoded[0] = typeByte;

        for(var i = 1; i < decoded.Length; i++)
            decoded[i] = (byte) (0x20 + i);

        return EncodeStandardBase58(decoded);
    }

    private static string CreateAlephiumP2MPKHAddress(IReadOnlyCollection<byte[]> publicKeyHashes, int threshold,
        bool appendTrailing = false)
    {
        var bytes = new List<byte> { 1 };
        bytes.AddRange(EncodeAlephiumCompactSignedInt(publicKeyHashes.Count));

        foreach(var publicKeyHash in publicKeyHashes)
        {
            if(publicKeyHash.Length != 32)
                throw new ArgumentException("Alephium public-key hash fixtures must be 32 bytes");

            bytes.AddRange(publicKeyHash);
        }

        bytes.AddRange(EncodeAlephiumCompactSignedInt(threshold));

        if(appendTrailing)
            bytes.Add(0xff);

        return EncodeStandardBase58(bytes.ToArray());
    }

    private static string CreateMalformedAlephiumP2MPKHAddress(int publicKeyHashCount, int hashBytesToWrite,
        int? threshold, bool appendTrailing)
    {
        var bytes = new List<byte> { 1 };
        bytes.AddRange(EncodeAlephiumCompactSignedInt(publicKeyHashCount));

        for(var i = 0; i < hashBytesToWrite; i++)
            bytes.Add((byte) (0x40 + i));

        if(threshold.HasValue)
            bytes.AddRange(EncodeAlephiumCompactSignedInt(threshold.Value));

        if(appendTrailing)
            bytes.Add(0xff);

        return EncodeStandardBase58(bytes.ToArray());
    }

    private static byte[] CreateAlephiumHash(byte seed)
    {
        var hash = new byte[32];
        for(var i = 0; i < hash.Length; i++)
            hash[i] = (byte) (seed + i);

        return hash;
    }

    private static byte[] EncodeAlephiumCompactSignedInt(int value)
    {
        if(value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));

        if(value < 0x20)
            return new[] { (byte) value };

        if(value < 0x2000)
            return new[] { (byte) ((value >> 8) + 0x40), (byte) value };

        if(value < 0x20000000)
        {
            return new[]
            {
                (byte) ((value >> 24) + 0x80),
                (byte) (value >> 16),
                (byte) (value >> 8),
                (byte) value
            };
        }

        return new[]
        {
            (byte) 0xc0,
            (byte) (value >> 24),
            (byte) (value >> 16),
            (byte) (value >> 8),
            (byte) value
        };
    }

    private static string EncodeStandardBase58(byte[] bytes)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new List<char>();

        while(value > BigInteger.Zero)
        {
            value = BigInteger.DivRem(value, 58, out var remainder);
            chars.Add(alphabet[(int) remainder]);
        }

        foreach(var b in bytes)
        {
            if(b != 0)
                break;

            chars.Add(alphabet[0]);
        }

        chars.Reverse();
        return chars.Count == 0 ? alphabet[0].ToString() : new string(chars.ToArray());
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
