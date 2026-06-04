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
        Assert.Equal(PayoutProfileConstants.Families.Bitcoin, plan.CoinFamily);
        Assert.Equal(PayoutProfileConstants.AdapterIds.BitcoinRpc, plan.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.BatchMultiRecipient, plan.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendMany, plan.SendMethod);
        Assert.Equal(PayoutProfileConstants.Families.Bitcoin, plan.Key.CoinFamily);
        Assert.Equal(PayoutProfileConstants.AdapterIds.BitcoinRpc, plan.Key.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.BatchMultiRecipient, plan.Key.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendMany, plan.Key.SendMethod);
        var route = Assert.Single(plan.Routes);
        Assert.Equal("pool-a", route.PoolId);
        Assert.Equal("bitcoin", route.Coin);
        Assert.True(route.AllowSendMany);
        Assert.True(route.AllowSendToAddress);
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
    public void CreatePlans_MultipleRoutesWithSameRegistryKeyAreGrouped()
    {
        var first = EnabledConfig(poolId: "pool-a", coin: "bitcoin");
        var second = EnabledConfig(poolId: "pool-b", coin: "bitcoin");

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { first, second }, Resolver(("bitcoin", BitcoinSendManyProfile())));

        Assert.True(result.IsValid);
        var plan = Assert.Single(result.Plans);
        Assert.Equal(PayoutProfileConstants.AdapterIds.BitcoinRpc, plan.Key.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.BatchMultiRecipient, plan.Key.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendMany, plan.Key.SendMethod);
        Assert.Equal(2, plan.Routes.Count);
        Assert.Contains(plan.Routes, x => x.PoolId == "pool-a" && x.Coin == "bitcoin");
        Assert.Contains(plan.Routes, x => x.PoolId == "pool-b" && x.Coin == "bitcoin");
    }

    [Fact]
    public void CreatePlans_PlanSafeSummaryDoesNotContainCredentialMaterial()
    {
        const string username = "rpc-user";
        const string password = "SUPER_SECRET_PASSWORD";
        const string endpoint = "http://127.0.0.1:18443";
        const string walletName = "secret-wallet";
        var config = EnabledConfig();
        config.Username = username;
        config.Password = password;
        config.Endpoint = endpoint;
        config.WalletName = walletName;

        var result = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(
            new[] { config }, Resolver(("bitcoin", BitcoinSendManyProfile())));

        Assert.True(result.IsValid);
        var plan = Assert.Single(result.Plans);
        var route = Assert.Single(plan.Routes);
        Assert.DoesNotContain(password, route.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(username, route.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, route.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(walletName, route.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", route.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("userinfo", route.SafeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WalletNameSet=True", route.SafeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateRegistrations_CreatesOneRegistrationPerGroupedPlanAndRoutesByPool()
    {
        var plan = new BitcoinRpcPayoutSenderRegistrationPlan
        {
            Key = new PayoutAttemptSenderKey
            {
                CoinFamily = PayoutProfileConstants.Families.Bitcoin,
                AdapterId = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                SendMethod = PayoutProfileConstants.SendMethods.SendMany
            },
            Routes = new[]
            {
                RoutePlan("pool-a", "bitcoin"),
                RoutePlan("pool-b", "bitcoin")
            }
        };
        var poolAClient = new FakeBitcoinPayoutRpcClient();
        var poolBClient = new FakeBitcoinPayoutRpcClient();
        var routeClients = new Dictionary<BitcoinPayoutRpcRouteKey, IBitcoinPayoutRpcClient>
        {
            [RouteKey("pool-a", "bitcoin")] = poolAClient,
            [RouteKey("pool-b", "bitcoin")] = poolBClient
        };

        var registrations = BitcoinRpcPayoutSenderRegistrationFactory.CreateRegistrations(new[] { plan }, routeClients);

        var registration = Assert.Single(registrations);
        Assert.Equal(plan.Key, registration.Key);
        var sender = Assert.IsType<BitcoinRpcPayoutSender>(registration.Sender);

        var result = await sender.SendAsync(SendManyContext("pool-b", "bitcoin"), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, result.Status);
        Assert.Equal(0, poolAClient.SendManyCallCount);
        Assert.Equal(1, poolBClient.SendManyCallCount);
    }

    [Fact]
    public void CreateRegistrations_MissingRouteClientFails()
    {
        var plan = new BitcoinRpcPayoutSenderRegistrationPlan
        {
            Key = new PayoutAttemptSenderKey
            {
                CoinFamily = PayoutProfileConstants.Families.Bitcoin,
                AdapterId = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                SendMethod = PayoutProfileConstants.SendMethods.SendMany
            },
            Routes = new[]
            {
                RoutePlan("pool-a", "bitcoin")
            }
        };

        Assert.Throws<InvalidOperationException>(() =>
            BitcoinRpcPayoutSenderRegistrationFactory.CreateRegistrations(new[] { plan },
                new Dictionary<BitcoinPayoutRpcRouteKey, IBitcoinPayoutRpcClient>()));
    }

    [Fact]
    public void CreateRegistrations_DuplicatePlannedRoutesFail()
    {
        var plan = new BitcoinRpcPayoutSenderRegistrationPlan
        {
            Key = new PayoutAttemptSenderKey
            {
                CoinFamily = PayoutProfileConstants.Families.Bitcoin,
                AdapterId = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                SendMethod = PayoutProfileConstants.SendMethods.SendMany
            },
            Routes = new[]
            {
                RoutePlan("pool-a", "bitcoin"),
                RoutePlan("pool-a", "bitcoin")
            }
        };
        var routeClients = new Dictionary<BitcoinPayoutRpcRouteKey, IBitcoinPayoutRpcClient>
        {
            [RouteKey("pool-a", "bitcoin")] = new FakeBitcoinPayoutRpcClient()
        };

        Assert.Throws<ArgumentException>(() =>
            BitcoinRpcPayoutSenderRegistrationFactory.CreateRegistrations(new[] { plan }, routeClients));
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

    private static BitcoinRpcPayoutSenderRoutePlan RoutePlan(string poolId, string coin)
    {
        return new BitcoinRpcPayoutSenderRoutePlan
        {
            PoolId = poolId,
            Coin = coin,
            AllowSendMany = true,
            AllowSendToAddress = true,
            SafeSummary = "EndpointSet=True; UsernameSet=True"
        };
    }

    private static BitcoinPayoutRpcRouteKey RouteKey(string poolId, string coin)
    {
        return new BitcoinPayoutRpcRouteKey
        {
            PoolId = poolId,
            Coin = coin
        };
    }

    private static PayoutSendExecutionContext SendManyContext(string poolId, string coin)
    {
        return new PayoutSendExecutionContext
        {
            Batch = new PayoutBatch
            {
                Id = 10,
                PoolId = poolId,
                Coin = coin,
                Handler = PayoutProfileConstants.AdapterIds.BitcoinRpc,
                SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient
            },
            Attempt = new PayoutSendAttempt
            {
                Id = 20,
                BatchId = 10,
                PoolId = poolId,
                Coin = coin,
                Method = PayoutProfileConstants.SendMethods.SendMany
            },
            Intents = new[]
            {
                new PayoutSendExecutionIntent
                {
                    IntentId = 30,
                    AttemptId = 20,
                    PoolId = poolId,
                    Coin = coin,
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
