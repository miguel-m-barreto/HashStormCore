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

public class BitcoinRpcPayoutSenderRegistrationMaterializerTests
{
    [Fact]
    public void Materialize_DisabledConfigProducesNoRegistrationsAndNoErrors()
    {
        var config = EnabledConfig();
        config.Enabled = false;
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            new TestHttpClientProvider());

        var result = materializer.Materialize(new[] { config });

        Assert.True(result.IsValid);
        Assert.Empty(result.Registrations);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Plans);
    }

    [Fact]
    public void Materialize_ValidSendManyConfigProducesOneRegistration()
    {
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            ProviderWith(RouteHandler("pool-a", "bitcoin")));

        var result = materializer.Materialize(new[] { EnabledConfig() });

        Assert.True(result.IsValid);
        var registration = Assert.Single(result.Registrations);
        Assert.Equal(PayoutProfileConstants.Families.Bitcoin, registration.Key.CoinFamily);
        Assert.Equal(PayoutProfileConstants.AdapterIds.BitcoinRpc, registration.Key.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.BatchMultiRecipient, registration.Key.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendMany, registration.Key.SendMethod);
        Assert.IsType<BitcoinRpcPayoutSender>(registration.Sender);
    }

    [Fact]
    public void Materialize_ValidSendToAddressConfigProducesOneRegistration()
    {
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendToAddressProfile())),
            ProviderWith(RouteHandler("pool-a", "bitcoin")));

        var result = materializer.Materialize(new[] { EnabledConfig() });

        Assert.True(result.IsValid);
        var registration = Assert.Single(result.Registrations);
        Assert.Equal(PayoutProfileConstants.SendShapes.PerAddress, registration.Key.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendToAddress, registration.Key.SendMethod);
    }

    [Fact]
    public void Materialize_TwoConfigsWithSameRegistryKeyProduceOneRegistrationWithTwoRoutes()
    {
        var first = EnabledConfig("pool-a", "bitcoin");
        var second = EnabledConfig("pool-b", "bitcoin");
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            ProviderWith(RouteHandler("pool-a", "bitcoin"), RouteHandler("pool-b", "bitcoin")));

        var result = materializer.Materialize(new[] { first, second });

        Assert.True(result.IsValid);
        Assert.Single(result.Registrations);
        var plan = Assert.Single(result.Plans);
        Assert.Equal(2, plan.Routes.Count);
        Assert.Contains(plan.Routes, x => x.PoolId == "pool-a" && x.Coin == "bitcoin");
        Assert.Contains(plan.Routes, x => x.PoolId == "pool-b" && x.Coin == "bitcoin");
    }

    [Fact]
    public void Materialize_RegistrationKeyMatchesPlannerProfileFields()
    {
        var profile = BitcoinSendManyProfile() with
        {
            CoinFamily = "custom-bitcoin-family"
        };
        var materializer = NewMaterializer(Resolver(("bitcoin", profile)),
            ProviderWith(RouteHandler("pool-a", "bitcoin")));

        var result = materializer.Materialize(new[] { EnabledConfig() });

        Assert.True(result.IsValid);
        var registration = Assert.Single(result.Registrations);
        Assert.Equal(profile.CoinFamily, registration.Key.CoinFamily);
        Assert.Equal(profile.AdapterId, registration.Key.AdapterId);
        Assert.Equal(profile.SendShape, registration.Key.SendShape);
        Assert.Equal(profile.SendMethod, registration.Key.SendMethod);
    }

    [Fact]
    public async Task Materialize_RoutesPoolAAndPoolBToTheirOwnFakeHttpClients()
    {
        var poolA = RouteHandler("pool-a", "bitcoin", "txid-pool-a");
        var poolB = RouteHandler("pool-b", "bitcoin", "txid-pool-b");
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            ProviderWith(poolA, poolB));

        var result = materializer.Materialize(new[]
        {
            EnabledConfig("pool-a", "bitcoin"),
            EnabledConfig("pool-b", "bitcoin")
        });
        var sender = Assert.IsType<BitcoinRpcPayoutSender>(Assert.Single(result.Registrations).Sender);

        var resultA = await sender.SendAsync(SendManyContext("pool-a", "bitcoin"), CancellationToken.None);
        var resultB = await sender.SendAsync(SendManyContext("pool-b", "bitcoin"), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, resultA.Status);
        Assert.Equal(PayoutAttemptSendStatus.Accepted, resultB.Status);
        Assert.Equal("txid-pool-a", resultA.Evidence.Value);
        Assert.Equal("txid-pool-b", resultB.Evidence.Value);
        Assert.Equal(1, poolA.Handler.CallCount);
        Assert.Equal(1, poolB.Handler.CallCount);
        Assert.Contains(@"""method"":""sendmany""", poolA.Handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains(@"""method"":""sendmany""", poolB.Handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Materialize_FullChainFakeHttpTxIdReturnsAccepted()
    {
        var route = RouteHandler("pool-a", "bitcoin", "txid-from-http");
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())), ProviderWith(route));

        var result = materializer.Materialize(new[] { EnabledConfig() });
        var sender = Assert.IsType<BitcoinRpcPayoutSender>(Assert.Single(result.Registrations).Sender);

        var sendResult = await sender.SendAsync(SendManyContext("pool-a", "bitcoin"), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, sendResult.Status);
        Assert.Equal(PayoutExternalConfirmationKinds.TxId, sendResult.Evidence.Kind);
        Assert.Equal("txid-from-http", sendResult.Evidence.Value);
        Assert.Equal(1, route.Handler.CallCount);
    }

    [Fact]
    public async Task Materialize_RouteOptionsReceiveWalletUnlockConfigForExactPoolAndCoin()
    {
        const string passphrase = "SUPER_SECRET_WALLET_PASSPHRASE";
        var config = EnabledConfig();
        config.WalletPassphrase = passphrase;
        config.WalletUnlockSeconds = 90;
        config.LockWalletAfterSend = false;
        var route = new TestRouteHandler("pool-a", "bitcoin", new FakeHandler(new[]
        {
            "{\"result\":null,\"error\":{\"code\":-13,\"message\":\"wallet locked\"},\"id\":\"1\"}",
            "{\"result\":null,\"error\":null,\"id\":\"2\"}",
            "{\"result\":\"txid-after-unlock\",\"error\":null,\"id\":\"3\"}"
        }));
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())), ProviderWith(route));

        var result = materializer.Materialize(new[] { config });
        var sender = Assert.IsType<BitcoinRpcPayoutSender>(Assert.Single(result.Registrations).Sender);

        var sendResult = await sender.SendAsync(SendManyContext("pool-a", "bitcoin"), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, sendResult.Status);
        Assert.Equal(new[] { "sendmany", "walletpassphrase", "sendmany" },
            route.Handler.Methods.ToArray());
        using var unlockParams = System.Text.Json.JsonDocument.Parse(route.Handler.RequestBodies[1]);
        var walletPassphraseParams = unlockParams.RootElement.GetProperty("params");
        Assert.Equal(passphrase, walletPassphraseParams[0].GetString());
        Assert.Equal(90, walletPassphraseParams[1].GetInt32());
    }

    [Fact]
    public void Materialize_MissingHttpClientReturnsStructuredErrorAndNoRegistrations()
    {
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            new TestHttpClientProvider());

        var result = materializer.Materialize(new[] { EnabledConfig() });

        Assert.False(result.IsValid);
        Assert.Empty(result.Registrations);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_materializer_missing_http_client");
    }

    [Fact]
    public void Materialize_PlannerValidationErrorReturnsStructuredErrorAndNoRegistrations()
    {
        var config = EnabledConfig();
        config.Endpoint = string.Empty;
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            ProviderWith(RouteHandler("pool-a", "bitcoin")));

        var result = materializer.Materialize(new[] { config });

        Assert.False(result.IsValid);
        Assert.Empty(result.Registrations);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_missing_endpoint");
    }

    [Fact]
    public void Materialize_EndpointWithUriUserInfoReturnsStructuredErrorAndNoRegistrations()
    {
        const string endpoint = "http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443";
        var config = EnabledConfig();
        config.Endpoint = endpoint;
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            ProviderWith(RouteHandler("pool-a", "bitcoin")));

        var result = materializer.Materialize(new[] { config });

        Assert.False(result.IsValid);
        Assert.Empty(result.Registrations);
        var error = Assert.Single(result.Errors);
        Assert.Equal("bitcoin_rpc_adapter_endpoint_contains_credentials", error.Code);
        AssertDoesNotLeak(result, endpoint, "rpc-user", "SUPER_SECRET_PASSWORD");
    }

    [Fact]
    public void Materialize_InvalidEndpointReturnsPlannerErrorAndNoRegistrations()
    {
        var config = EnabledConfig();
        config.Endpoint = "not an absolute uri";
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            ProviderWith(RouteHandler("pool-a", "bitcoin")));

        var result = materializer.Materialize(new[] { config });

        Assert.False(result.IsValid);
        Assert.Empty(result.Registrations);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_endpoint_invalid");
        AssertDoesNotLeak(result, config.Endpoint, config.Username, config.Password, config.WalletName);
    }

    [Fact]
    public void Materialize_NoPartialRegistrationsWhenOneRouteFails()
    {
        var poolA = RouteHandler("pool-a", "bitcoin");
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            ProviderWith(poolA));

        var result = materializer.Materialize(new[]
        {
            EnabledConfig("pool-a", "bitcoin"),
            EnabledConfig("pool-b", "bitcoin")
        });

        Assert.False(result.IsValid);
        Assert.Empty(result.Registrations);
        Assert.Contains(result.Errors, x => x.PoolId == "pool-b" &&
                                            x.Code == "bitcoin_rpc_materializer_missing_http_client");
    }

    [Fact]
    public void Materialize_ResultSummaryAndErrorsDoNotLeakCredentialMaterial()
    {
        const string endpoint = "not an absolute uri";
        const string username = "rpc-user";
        const string password = "SUPER_SECRET_PASSWORD";
        const string walletName = "secret-wallet";
        var config = EnabledConfig();
        config.Endpoint = endpoint;
        config.Username = username;
        config.Password = password;
        config.WalletName = walletName;
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            ProviderWith(RouteHandler("pool-a", "bitcoin")));

        var result = materializer.Materialize(new[] { config });

        AssertDoesNotLeak(result, endpoint, username, password, walletName);
        AssertDoesNotLeak(result.ToString(), endpoint, username, password, walletName);
        Assert.All(result.Errors, error =>
        {
            AssertDoesNotLeak(error.ToString(), endpoint, username, password, walletName);
            AssertDoesNotLeak(error.Message, endpoint, username, password, walletName);
        });
    }

    [Fact]
    public void Materialize_ResultSummaryAndErrorsDoNotLeakWalletPassphrase()
    {
        const string passphrase = "SUPER_SECRET_WALLET_PASSPHRASE";
        var config = EnabledConfig();
        config.WalletPassphrase = passphrase;
        config.WalletUnlockSeconds = 0;
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())),
            ProviderWith(RouteHandler("pool-a", "bitcoin")));

        var result = materializer.Materialize(new[] { config });

        Assert.False(result.IsValid);
        Assert.Empty(result.Registrations);
        Assert.Contains(result.Errors,
            x => x.Code == "bitcoin_rpc_adapter_wallet_passphrase_requires_unlock_seconds");
        AssertDoesNotLeak(result, passphrase);
    }

    [Fact]
    public void Materialize_HttpClientProviderRouteSummaryDoesNotLeakCredentialMaterial()
    {
        const string endpoint = "http://127.0.0.1:18443";
        const string username = "rpc-user";
        const string password = "SUPER_SECRET_PASSWORD";
        const string walletName = "secret-wallet";
        var config = EnabledConfig();
        config.Endpoint = endpoint;
        config.Username = username;
        config.Password = password;
        config.WalletName = walletName;
        var provider = ProviderWith(RouteHandler("pool-a", "bitcoin"));
        var materializer = NewMaterializer(Resolver(("bitcoin", BitcoinSendManyProfile())), provider);

        var result = materializer.Materialize(new[] { config });

        Assert.True(result.IsValid);
        var route = Assert.Single(provider.RequestedRoutes);
        AssertDoesNotLeak(route.ToString(), endpoint, username, password, walletName);
        AssertDoesNotLeak(route.SafeSummary, endpoint, username, password, walletName);
    }

    private static BitcoinRpcPayoutSenderRegistrationMaterializer NewMaterializer(
        IPayoutProfileResolver resolver,
        IBitcoinJsonRpcHttpClientProvider provider)
    {
        return new BitcoinRpcPayoutSenderRegistrationMaterializer(resolver, provider);
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
            WalletName = "wallet-a",
            RequestTimeoutSeconds = 30,
            AllowSendMany = true,
            AllowSendToAddress = true
        };
    }

    private static TestRouteHandler RouteHandler(
        string poolId,
        string coin,
        string txId = "txid-default")
    {
        return new TestRouteHandler(poolId, coin, new FakeHandler(txId));
    }

    private static TestHttpClientProvider ProviderWith(params TestRouteHandler[] routes)
    {
        return new TestHttpClientProvider(routes);
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

    private static void AssertDoesNotLeak(
        BitcoinRpcPayoutSenderRegistrationMaterializationResult result,
        params string[] secrets)
    {
        AssertDoesNotLeak(result.ToSafeSummary(), secrets);
        Assert.All(result.Errors, error =>
        {
            AssertDoesNotLeak(error.Code, secrets);
            AssertDoesNotLeak(error.Message, secrets);
            AssertDoesNotLeak(error.ToSafeSummary(), secrets);
        });
        Assert.All(result.Plans.SelectMany(x => x.Routes), route =>
            AssertDoesNotLeak(route.SafeSummary, secrets));
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

    private sealed class TestHttpClientProvider : IBitcoinJsonRpcHttpClientProvider
    {
        public TestHttpClientProvider(params TestRouteHandler[] routes)
        {
            foreach(var route in routes)
                clients.Add($"{route.PoolId}\n{route.Coin}", new HttpClient(route.Handler));
        }

        private readonly Dictionary<string, HttpClient> clients = new(StringComparer.OrdinalIgnoreCase);
        public List<BitcoinJsonRpcHttpClientRoute> RequestedRoutes { get; } = new();

        public HttpClient GetHttpClient(BitcoinJsonRpcHttpClientRoute route)
        {
            RequestedRoutes.Add(route);
            return clients.TryGetValue($"{route.PoolId}\n{route.Coin}", out var client) ? client : null;
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public FakeHandler(string txId)
        {
            this.txId = txId;
        }

        public FakeHandler(IEnumerable<string> responseBodies)
        {
            txId = "txid-default";
            responses = new Queue<string>(responseBodies);
        }

        private readonly string txId;
        private readonly Queue<string> responses = new();
        public int CallCount { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;
        public List<string> RequestBodies { get; } = new();
        public List<string> Methods { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(LastRequestBody);
            Methods.Add(ReadMethod(LastRequestBody));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Count > 0
                    ? responses.Dequeue()
                    : "{\"result\":\"" + txId + "\",\"error\":null,\"id\":\"1\"}")
            };
        }

        private static string ReadMethod(string requestBody)
        {
            if(string.IsNullOrWhiteSpace(requestBody))
                return string.Empty;

            using var document = System.Text.Json.JsonDocument.Parse(requestBody);
            return document.RootElement.GetProperty("method").GetString();
        }
    }
}
