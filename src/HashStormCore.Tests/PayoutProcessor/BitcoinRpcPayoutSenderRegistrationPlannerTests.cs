using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Payments;
using HashStormCore.PayoutProcessor.Configuration;
using HashStormCore.Payouts.Bitcoin;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using Xunit;

namespace HashStormCore.Tests.PayoutProcessor;

public class BitcoinRpcPayoutSenderRegistrationPlannerTests
{
    [Fact]
    public void CreatePlans_DisabledConfigProducesNoPlanAndNoError()
    {
        var resolver = Resolver(("bitcoin", BitcoinSendManyProfile()));
        var config = EnabledConfig();
        config.Enabled = false;

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(new[] { config }, resolver);

        Assert.True(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void CreatePlans_ValidSendManyProfileProducesExactProfileKey()
    {
        var resolver = Resolver(("bitcoin", BitcoinSendManyProfile()));

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { EnabledConfig() }, resolver);

        Assert.True(result.IsValid);
        var plan = Assert.Single(result.Plans);
        Assert.Equal("pool-a", plan.PoolId);
        Assert.Equal("bitcoin", plan.Coin);
        Assert.Equal(PayoutProfileConstants.Families.Bitcoin, plan.CoinFamily);
        Assert.Equal(PayoutProfileConstants.AdapterIds.BitcoinRpc, plan.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.BatchMultiRecipient, plan.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendMany, plan.SendMethod);
        Assert.Equal(PayoutProfileConstants.Families.Bitcoin, plan.Key.CoinFamily);
        Assert.Equal(PayoutProfileConstants.AdapterIds.BitcoinRpc, plan.Key.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.BatchMultiRecipient, plan.Key.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendMany, plan.Key.SendMethod);
    }

    [Fact]
    public void CreatePlans_ValidSendToAddressProfileProducesExactProfileKey()
    {
        var resolver = Resolver(("bitcoin", BitcoinSendToAddressProfile()));

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { EnabledConfig() }, resolver);

        Assert.True(result.IsValid);
        var plan = Assert.Single(result.Plans);
        Assert.Equal(PayoutProfileConstants.AdapterIds.BitcoinRpc, plan.Key.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.PerAddress, plan.Key.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendToAddress, plan.Key.SendMethod);
    }

    [Fact]
    public void CreatePlans_NonBitcoinRpcProfileReturnsStructuredError()
    {
        var resolver = Resolver(("kaspa", BitcoinSendManyProfile() with
        {
            AdapterId = PayoutProfileConstants.AdapterIds.KaspaWalletWrapper
        }));

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { EnabledConfig(coin: "kaspa") }, resolver);

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_profile_not_bitcoin_rpc");
    }

    [Fact]
    public void CreatePlans_UnresolvedCoinReturnsStructuredError()
    {
        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { EnabledConfig(coin: "missing") }, Resolver());

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_profile_unresolved");
    }

    [Fact]
    public void CreatePlans_NotReadyProfileReturnsStructuredError()
    {
        var resolver = Resolver(("bitcoin", BitcoinSendManyProfile() with
        {
            ReservationReady = false,
            NotReadyReason = "not ready"
        }));

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { EnabledConfig() }, resolver);

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_profile_not_ready");
    }

    [Fact]
    public void CreatePlans_SendManyDisabledForSendManyProfileReturnsStructuredError()
    {
        var resolver = Resolver(("bitcoin", BitcoinSendManyProfile()));
        var config = EnabledConfig();
        config.AllowSendMany = false;

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(new[] { config }, resolver);

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_sendmany_disabled");
    }

    [Fact]
    public void CreatePlans_SendToAddressDisabledForSendToAddressProfileReturnsStructuredError()
    {
        var resolver = Resolver(("bitcoin", BitcoinSendToAddressProfile()));
        var config = EnabledConfig();
        config.AllowSendToAddress = false;

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(new[] { config }, resolver);

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_sendtoaddress_disabled");
    }

    [Fact]
    public void CreatePlans_ConfigValidationErrorsAreSurfaced()
    {
        var config = EnabledConfig();
        config.Endpoint = string.Empty;

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { config }, Resolver(("bitcoin", BitcoinSendManyProfile())));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_missing_endpoint");
    }

    [Fact]
    public void CreatePlans_DuplicateEnabledPoolAndCoinSurfacesConfigValidationError()
    {
        var first = EnabledConfig();
        var second = EnabledConfig();

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { first, second }, Resolver(("bitcoin", BitcoinSendManyProfile())));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_duplicate_pool_coin");
    }

    [Fact]
    public void CreatePlans_DuplicatePlannedRegistryKeyFailsClosed()
    {
        var first = EnabledConfig(poolId: "pool-a", coin: "bitcoin");
        var second = EnabledConfig(poolId: "pool-b", coin: "bitcoin");

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { first, second }, Resolver(("bitcoin", BitcoinSendManyProfile())));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_duplicate_registry_key");
    }

    [Fact]
    public void CreatePlans_PlanSafeSummaryDoesNotContainCredentialMaterial()
    {
        const string username = "rpc-user";
        const string password = "SUPER_SECRET_PASSWORD";
        const string endpoint = "http://127.0.0.1:18443";
        var config = EnabledConfig();
        config.Username = username;
        config.Password = password;
        config.Endpoint = endpoint;

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { config }, Resolver(("bitcoin", BitcoinSendManyProfile())));

        Assert.True(result.IsValid);
        var plan = Assert.Single(result.Plans);
        Assert.DoesNotContain(password, plan.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(username, plan.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, plan.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", plan.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("userinfo", plan.SafeSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRegistrations_UsesInjectedClientAndPlanKey()
    {
        var plan = new BitcoinRpcPayoutSenderRegistrationPlan
        {
            Key = new PayoutAttemptSenderKey
            {
                CoinFamily = PayoutProfileConstants.Families.Bitcoin,
                AdapterId = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                SendMethod = PayoutProfileConstants.SendMethods.SendMany
            }
        };
        var client = new FakeBitcoinPayoutRpcClient();

        var registrations = BitcoinRpcPayoutSenderRegistrationFactory.CreateRegistrations(new[] { plan }, client);

        var registration = Assert.Single(registrations);
        Assert.Equal(plan.Key, registration.Key);
        var sender = Assert.IsType<BitcoinRpcPayoutSender>(registration.Sender);

        var result = await sender.SendAsync(SendManyContext(), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, result.Status);
        Assert.Equal(1, client.SendManyCallCount);
    }

    private static PayoutProcessorBitcoinRpcAdapterConfig EnabledConfig(
        string poolId = "pool-a",
        string coin = "bitcoin")
    {
        return new PayoutProcessorBitcoinRpcAdapterConfig
        {
            Enabled = true,
            PoolId = poolId,
            Coin = coin,
            Endpoint = "http://127.0.0.1:18443",
            Username = "rpc-user",
            Password = "rpc-password",
            RequestTimeoutSeconds = 30,
            AllowSendMany = true,
            AllowSendToAddress = true
        };
    }

    private static PayoutProfile BitcoinSendManyProfile()
    {
        return new PayoutProfile
        {
            CoinKey = "bitcoin",
            CoinSymbol = "BTC",
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

    private static PayoutProfile BitcoinSendToAddressProfile()
    {
        return BitcoinSendManyProfile() with
        {
            SendShape = PayoutProfileConstants.SendShapes.PerAddress,
            SendMethod = PayoutProfileConstants.SendMethods.SendToAddress,
            AllowsBatchMultiRecipient = false,
            AllowsPerAddress = true
        };
    }

    private static TestProfileResolver Resolver(params (string Coin, PayoutProfile Profile)[] profiles)
    {
        return new TestProfileResolver(profiles);
    }

    private static PayoutSendExecutionContext SendManyContext()
    {
        return new PayoutSendExecutionContext
        {
            Batch = new PayoutBatch
            {
                Id = 10,
                PoolId = "pool-a",
                Coin = "bitcoin",
                Handler = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient
            },
            Attempt = new PayoutSendAttempt
            {
                Id = 20,
                BatchId = 10,
                PoolId = "pool-a",
                Coin = "bitcoin",
                Method = PayoutProfileConstants.SendMethods.SendMany
            },
            Intents = new[]
            {
                new PayoutSendExecutionIntent
                {
                    IntentId = 30,
                    AttemptId = 20,
                    PoolId = "pool-a",
                    Coin = "bitcoin",
                    Address = "addr-a",
                    Amount = 1m,
                    IntentState = PayoutIntentStates.Reserved,
                    AttemptIntentState = PayoutAttemptIntentStates.Active
                }
            }
        };
    }

    private sealed class TestProfileResolver : IPayoutProfileResolver
    {
        public TestProfileResolver(IEnumerable<(string Coin, PayoutProfile Profile)> profiles)
        {
            this.profiles = profiles.ToDictionary(x => x.Coin, x => x.Profile, StringComparer.OrdinalIgnoreCase);
        }

        private readonly IReadOnlyDictionary<string, PayoutProfile> profiles;

        public PayoutProfileResolution Resolve(string coinKey)
        {
            return profiles.TryGetValue(coinKey, out var profile)
                ? PayoutProfileResolution.Resolved(profile)
                : PayoutProfileResolution.Unsupported("unsupported coin");
        }
    }

    private sealed class FakeBitcoinPayoutRpcClient : IBitcoinPayoutRpcClient
    {
        public int SendManyCallCount { get; private set; }

        public Task<BitcoinPayoutRpcResult> SendManyAsync(BitcoinPayoutSendManyRequest request, CancellationToken ct)
        {
            SendManyCallCount++;
            return Task.FromResult(BitcoinPayoutRpcResult.Accepted("txid-from-injected-client"));
        }

        public Task<BitcoinPayoutRpcResult> SendToAddressAsync(BitcoinPayoutSendToAddressRequest request,
            CancellationToken ct)
        {
            throw new NotSupportedException();
        }
    }
}
