using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Payments;
using HashStormCore.PayoutProcessor.Configuration;
using HashStormCore.Payouts.Bitcoin;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using Xunit;

namespace HashStormCore.Tests.PayoutProcessor;

public class BitcoinRpcPayoutSenderRegistryBuilderTests
{
    [Fact]
    public void Build_DefaultConfigReturnsEmptyRegistry()
    {
        var registry = Build(new PayoutProcessorConfig());

        AssertEmpty(registry);
    }

    [Fact]
    public void BuildWithReport_DefaultConfigReportsProcessorDisabled()
    {
        var result = BuildWithReport(new PayoutProcessorConfig());

        AssertEmpty(result.Registry);
        Assert.Equal("empty", result.Report.Status);
        Assert.Equal("bitcoin_rpc_sender_registry_processor_disabled", result.Report.ReasonCode);
        Assert.Equal(0, result.Report.RegistrationCount);
        Assert.Equal(0, result.Report.RouteCount);
    }

    [Fact]
    public void Build_DisabledProcessorReturnsEmptyRegistry()
    {
        var config = EnabledSidecarConfig();
        config.Enabled = false;

        var registry = Build(config);

        AssertEmpty(registry);
    }

    [Fact]
    public void Build_DisabledModeReturnsEmptyRegistry()
    {
        var config = EnabledSidecarConfig();
        config.Mode = PayoutProcessorMode.Disabled;

        var registry = Build(config);

        AssertEmpty(registry);
    }

    [Fact]
    public void Build_DryRunReturnsEmptyRegistryEvenWithEnabledBitcoinRpcAdapter()
    {
        var config = EnabledSidecarConfig();
        config.Mode = PayoutProcessorMode.DryRun;
        var provider = new ThrowingHttpClientProvider();

        var registry = Build(config, provider: provider);

        AssertEmpty(registry);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void BuildWithReport_DryRunReportsSafeEmptyReason()
    {
        var config = EnabledSidecarConfig();
        config.Mode = PayoutProcessorMode.DryRun;

        var result = BuildWithReport(config);

        AssertEmpty(result.Registry);
        Assert.Equal("bitcoin_rpc_sender_registry_dry_run", result.Report.ReasonCode);
        Assert.Equal(1, result.Report.EnabledAdapterCount);
    }

    [Fact]
    public void Build_FakeAdaptersOnlyReturnsEmptyRegistryEvenWithEnabledBitcoinRpcAdapter()
    {
        var config = EnabledSidecarConfig();
        config.FakeAdaptersOnly = true;
        var provider = new ThrowingHttpClientProvider();

        var registry = Build(config, provider: provider);

        AssertEmpty(registry);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void BuildWithReport_FakeAdaptersOnlyReportsSafeEmptyReason()
    {
        var config = EnabledSidecarConfig();
        config.FakeAdaptersOnly = true;

        var result = BuildWithReport(config);

        AssertEmpty(result.Registry);
        Assert.Equal("bitcoin_rpc_sender_registry_fake_adapters_only", result.Report.ReasonCode);
        Assert.Equal(1, result.Report.EnabledAdapterCount);
    }

    [Fact]
    public void Build_NoEnabledBitcoinRpcAdaptersReturnsEmptyRegistry()
    {
        var config = EnabledSidecarConfig();
        var disabledAdapter = EnabledAdapter();
        disabledAdapter.Enabled = false;
        disabledAdapter.Endpoint = "http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443";
        disabledAdapter.Username = string.Empty;
        disabledAdapter.Password = string.Empty;
        config.BitcoinRpcAdapters = new[]
        {
            disabledAdapter
        };

        var registry = Build(config);

        AssertEmpty(registry);
    }

    [Fact]
    public void Build_ValidEnabledAdapterWithMatchingIntentPoolReturnsSenderRegistry()
    {
        var registry = Build(EnabledSidecarConfig());

        Assert.True(registry.TryGetSender(BitcoinSendManyProfile(), out var sender));
        Assert.IsType<BitcoinRpcPayoutSender>(sender);
    }

    [Fact]
    public void BuildWithReport_ValidEnabledAdapterReportsMaterializedSummaryWithoutSecrets()
    {
        const string endpoint = "http://127.0.0.1:18443";
        const string username = "rpc-user";
        const string password = "rpc-password";
        const string walletName = "wallet-a";
        var config = EnabledSidecarConfig();
        config.BitcoinRpcAdapters[0].Endpoint = endpoint;
        config.BitcoinRpcAdapters[0].Username = username;
        config.BitcoinRpcAdapters[0].Password = password;
        config.BitcoinRpcAdapters[0].WalletName = walletName;

        var result = BuildWithReport(config);

        Assert.True(result.Registry.TryGetSender(BitcoinSendManyProfile(), out _));
        Assert.Equal("materialized", result.Report.Status);
        Assert.Equal("bitcoin_rpc_sender_registry_materialized", result.Report.ReasonCode);
        Assert.Equal(1, result.Report.EnabledAdapterCount);
        Assert.Equal(1, result.Report.RegistrationCount);
        Assert.Equal(1, result.Report.RouteCount);
        var route = Assert.Single(result.Report.Routes);
        Assert.Equal("pool-a", route.PoolId);
        Assert.Equal("bitcoin", route.Coin);
        AssertDoesNotLeak(result.Report.ToSafeSummary(), endpoint, username, password, walletName, "127.0.0.1");
        AssertDoesNotLeak(route.ToSafeSummary(), endpoint, username, password, walletName, "127.0.0.1");
    }

    [Fact]
    public async Task Build_ValidRegistryCanExecuteThroughFakeHttpToAcceptedTxId()
    {
        var handler = new FakeHandler("txid-from-program-wiring-helper");
        var registry = Build(EnabledSidecarConfig(), provider: ProviderWith(RouteHandler("pool-a", "bitcoin", handler)));
        Assert.True(registry.TryGetSender(BitcoinSendManyProfile(), out var sender));

        var result = await sender.SendAsync(SendManyContext("pool-a", "bitcoin"), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, result.Status);
        Assert.Equal(PayoutExternalConfirmationKinds.TxId, result.Evidence.Kind);
        Assert.Equal("txid-from-program-wiring-helper", result.Evidence.Value);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Build_MultipleEnabledRoutesWithSameRegistryKeyShareOneSenderAndRouteByPool()
    {
        var config = EnabledSidecarConfig();
        config.BitcoinRpcAdapters = new[]
        {
            EnabledAdapter("pool-a", "bitcoin"),
            EnabledAdapter("pool-b", "bitcoin")
        };
        var cluster = new PayoutProcessorClusterConfig
        {
            Pools = new[]
            {
                ClusterPoolEntry("pool-a", "bitcoin"),
                ClusterPoolEntry("pool-b", "bitcoin")
            }
        };
        var poolAHandler = new FakeHandler("txid-pool-a");
        var poolBHandler = new FakeHandler("txid-pool-b");
        var registry = Build(config, cluster, provider: ProviderWith(
            RouteHandler("pool-a", "bitcoin", poolAHandler),
            RouteHandler("pool-b", "bitcoin", poolBHandler)));
        Assert.True(registry.TryGetSender(BitcoinSendManyProfile(), out var sender));

        var poolAResult = await sender.SendAsync(SendManyContext("pool-a", "bitcoin"), CancellationToken.None);
        var poolBResult = await sender.SendAsync(SendManyContext("pool-b", "bitcoin"), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, poolAResult.Status);
        Assert.Equal(PayoutAttemptSendStatus.Accepted, poolBResult.Status);
        Assert.Equal("txid-pool-a", poolAResult.Evidence.Value);
        Assert.Equal("txid-pool-b", poolBResult.Evidence.Value);
        Assert.Equal(1, poolAHandler.CallCount);
        Assert.Equal(1, poolBHandler.CallCount);
    }

    [Theory]
    [InlineData("bitcoin_rpc_sender_registry_pool_missing")]
    public void Build_EnabledAdapterForMissingPoolFailsStartup(string expectedCode)
    {
        var cluster = new PayoutProcessorClusterConfig
        {
            Pools = Array.Empty<PayoutProcessorClusterPoolConfig>()
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Build(EnabledSidecarConfig(), cluster));

        Assert.Contains(expectedCode, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EnabledAdapterForDisabledPoolFailsStartup()
    {
        var cluster = ClusterPool(enabled: false);

        var ex = Assert.Throws<InvalidOperationException>(() => Build(EnabledSidecarConfig(), cluster));

        Assert.Contains("bitcoin_rpc_sender_registry_pool_disabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EnabledAdapterForPoolWithPaymentProcessingDisabledFailsStartup()
    {
        var cluster = ClusterPool(paymentProcessingEnabled: false);

        var ex = Assert.Throws<InvalidOperationException>(() => Build(EnabledSidecarConfig(), cluster));

        Assert.Contains("bitcoin_rpc_sender_registry_pool_payment_processing_disabled", ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EnabledAdapterForPoolWithoutPaymentProcessingFailsStartup()
    {
        var cluster = ClusterPool();
        cluster.Pools[0].PaymentProcessing = null;

        var ex = Assert.Throws<InvalidOperationException>(() => Build(EnabledSidecarConfig(), cluster));

        Assert.Contains("bitcoin_rpc_sender_registry_pool_payment_processing_missing", ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EnabledAdapterForPoolWithNonIntentEngineFailsStartup()
    {
        var cluster = ClusterPool(engine: "legacy");

        var ex = Assert.Throws<InvalidOperationException>(() => Build(EnabledSidecarConfig(), cluster));

        Assert.Contains("bitcoin_rpc_sender_registry_pool_engine_not_intent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EnabledAdapterWithPoolCoinMismatchFailsStartup()
    {
        var cluster = ClusterPool(coin: "litecoin");

        var ex = Assert.Throws<InvalidOperationException>(() => Build(EnabledSidecarConfig(), cluster));

        Assert.Contains("bitcoin_rpc_sender_registry_pool_coin_mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EnabledInvalidAdapterConfigFailsStartup()
    {
        var config = EnabledSidecarConfig();
        config.BitcoinRpcAdapters[0].Endpoint = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(() => Build(config));

        Assert.Contains("bitcoin_rpc_adapter_missing_endpoint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MaterializerErrorsFailStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(EnabledSidecarConfig(), resolver: Resolver()));

        Assert.Contains("bitcoin_rpc_adapter_profile_unresolved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_OneBadEnabledAdapterPreventsPartialRegistry()
    {
        var config = EnabledSidecarConfig();
        config.BitcoinRpcAdapters = new[]
        {
            EnabledAdapter("pool-a", "bitcoin"),
            EnabledAdapter("missing-pool", "bitcoin")
        };
        var provider = ProviderWith(RouteHandler("pool-a", "bitcoin", new FakeHandler("txid-pool-a")));

        var ex = Assert.Throws<InvalidOperationException>(() => Build(config, provider: provider));

        Assert.Contains("bitcoin_rpc_sender_registry_pool_missing", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, provider.RequestedRoutes.Count);
    }

    [Fact]
    public void Build_DisabledInvalidAdapterDoesNotFailStartup()
    {
        var config = EnabledSidecarConfig();
        var disabledAdapter = EnabledAdapter();
        disabledAdapter.Enabled = false;
        disabledAdapter.Endpoint = "http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443";
        disabledAdapter.Username = string.Empty;
        disabledAdapter.Password = string.Empty;
        config.BitcoinRpcAdapters = new[]
        {
            disabledAdapter
        };

        var registry = Build(config);

        AssertEmpty(registry);
    }

    [Fact]
    public void Build_StartupFailureMessageDoesNotLeakCredentialMaterial()
    {
        const string endpoint = "http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443";
        const string username = "rpc-user";
        const string password = "SUPER_SECRET_PASSWORD";
        const string walletName = "secret-wallet";
        var config = EnabledSidecarConfig();
        config.BitcoinRpcAdapters[0].Endpoint = endpoint;
        config.BitcoinRpcAdapters[0].Username = username;
        config.BitcoinRpcAdapters[0].Password = password;
        config.BitcoinRpcAdapters[0].WalletName = walletName;

        var ex = Assert.Throws<InvalidOperationException>(() => Build(config));

        Assert.Contains("bitcoin_rpc_adapter_endpoint_contains_credentials", ex.Message, StringComparison.Ordinal);
        AssertDoesNotLeak(ex.Message, endpoint, username, password, walletName, "127.0.0.1");
    }

    [Fact]
    public void DefaultHttpClientProviderUsesNamedClientAndSafeToString()
    {
        var httpClient = new HttpClient(new FakeHandler("txid"));
        var factory = new FakeHttpClientFactory(httpClient);
        var provider = new DefaultBitcoinJsonRpcHttpClientProvider(factory);

        var client = provider.GetHttpClient(new BitcoinJsonRpcHttpClientRoute
        {
            PoolId = "pool-a",
            Coin = "bitcoin",
            SafeSummary = "EndpointSet=True; UsernameSet=True; WalletNameSet=True"
        });

        Assert.Same(httpClient, client);
        Assert.Equal(DefaultBitcoinJsonRpcHttpClientProvider.ClientName, factory.LastName);
        Assert.Equal(nameof(DefaultBitcoinJsonRpcHttpClientProvider), provider.ToString());
    }

    private static IPayoutAttemptSenderRegistry Build(
        PayoutProcessorConfig config,
        PayoutProcessorClusterConfig cluster = null,
        IPayoutProfileResolver resolver = null,
        TestHttpClientProvider provider = null)
    {
        var materializer = new BitcoinRpcPayoutSenderRegistrationMaterializer(
            resolver ?? Resolver(("bitcoin", BitcoinSendManyProfile())),
            provider ?? ProviderWith(RouteHandler("pool-a", "bitcoin", new FakeHandler("txid-default"))));

        return BitcoinRpcPayoutSenderRegistryBuilder.Build(
            config,
            cluster ?? ClusterPool(),
            materializer);
    }

    private static BitcoinRpcPayoutSenderRegistryBuildResult BuildWithReport(
        PayoutProcessorConfig config,
        PayoutProcessorClusterConfig cluster = null,
        IPayoutProfileResolver resolver = null,
        TestHttpClientProvider provider = null)
    {
        var materializer = new BitcoinRpcPayoutSenderRegistrationMaterializer(
            resolver ?? Resolver(("bitcoin", BitcoinSendManyProfile())),
            provider ?? ProviderWith(RouteHandler("pool-a", "bitcoin", new FakeHandler("txid-default"))));

        return BitcoinRpcPayoutSenderRegistryBuilder.BuildWithReport(
            config,
            cluster ?? ClusterPool(),
            materializer);
    }

    private static PayoutProcessorConfig EnabledSidecarConfig()
    {
        return new PayoutProcessorConfig
        {
            Enabled = true,
            Mode = PayoutProcessorMode.DbMutating,
            FakeAdaptersOnly = false,
            BitcoinRpcAdapters = new[]
            {
                EnabledAdapter()
            }
        };
    }

    private static PayoutProcessorBitcoinRpcAdapterConfig EnabledAdapter(
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
            WalletName = "wallet-a",
            RequestTimeoutSeconds = 30,
            AllowSendMany = true,
            AllowSendToAddress = true
        };
    }

    private static PayoutProcessorClusterConfig ClusterPool(
        bool enabled = true,
        bool paymentProcessingEnabled = true,
        string engine = "intent",
        string coin = "bitcoin")
    {
        return new PayoutProcessorClusterConfig
        {
            Pools = new[]
            {
                ClusterPoolEntry("pool-a", coin, enabled, paymentProcessingEnabled, engine)
            }
        };
    }

    private static PayoutProcessorClusterPoolConfig ClusterPoolEntry(
        string poolId,
        string coin,
        bool enabled = true,
        bool paymentProcessingEnabled = true,
        string engine = "intent")
    {
        return new PayoutProcessorClusterPoolConfig
        {
            Id = poolId,
            Enabled = enabled,
            Coin = coin,
            PaymentProcessing = new PayoutProcessorClusterPaymentProcessingConfig
            {
                Enabled = paymentProcessingEnabled,
                Engine = engine
            }
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

    private static TestProfileResolver Resolver(params (string Coin, PayoutProfile Profile)[] profiles)
    {
        return new TestProfileResolver(profiles);
    }

    private static TestRouteHandler RouteHandler(string poolId, string coin, FakeHandler handler)
    {
        return new TestRouteHandler(poolId, coin, handler);
    }

    private static TestHttpClientProvider ProviderWith(params TestRouteHandler[] routes)
    {
        return new TestHttpClientProvider(routes);
    }

    private static void AssertEmpty(IPayoutAttemptSenderRegistry registry)
    {
        Assert.False(registry.TryGetSender(BitcoinSendManyProfile(), out _));
    }

    private static void AssertDoesNotLeak(string value, params string[] secrets)
    {
        Assert.All(secrets.Where(x => !string.IsNullOrWhiteSpace(x)), secret =>
            Assert.DoesNotContain(secret, value, StringComparison.Ordinal));
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

    private sealed record TestRouteHandler(string PoolId, string Coin, FakeHandler Handler);

    private class TestHttpClientProvider : IBitcoinJsonRpcHttpClientProvider
    {
        public TestHttpClientProvider(params TestRouteHandler[] routes)
        {
            foreach(var route in routes)
                clients.Add($"{route.PoolId}\n{route.Coin}", new HttpClient(route.Handler));
        }

        private readonly Dictionary<string, HttpClient> clients = new(StringComparer.OrdinalIgnoreCase);
        public List<BitcoinJsonRpcHttpClientRoute> RequestedRoutes { get; } = new();

        public virtual HttpClient GetHttpClient(BitcoinJsonRpcHttpClientRoute route)
        {
            RequestedRoutes.Add(route);
            return clients.TryGetValue($"{route.PoolId}\n{route.Coin}", out var client) ? client : null;
        }
    }

    private sealed class ThrowingHttpClientProvider : TestHttpClientProvider
    {
        public int CallCount { get; private set; }

        public override HttpClient GetHttpClient(BitcoinJsonRpcHttpClientRoute route)
        {
            CallCount++;
            throw new InvalidOperationException("provider should not be called");
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public FakeHandler(string txId)
        {
            this.txId = txId;
        }

        private readonly string txId;
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":\"" + txId + "\",\"error\":null,\"id\":\"1\"}")
            });
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public FakeHttpClientFactory(HttpClient client)
        {
            this.client = client;
        }

        private readonly HttpClient client;
        public string LastName { get; private set; } = string.Empty;

        public HttpClient CreateClient(string name)
        {
            LastName = name;
            return client;
        }
    }
}
