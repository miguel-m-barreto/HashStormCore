using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Payouts.Bitcoin;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class BitcoinJsonRpcHttpTransportTests
{
    [Fact]
    public void Constructor_NullHttpClientThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new BitcoinJsonRpcHttpTransport(null));
    }

    [Fact]
    public async Task SendAsync_NullRequestThrows()
    {
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(new FakeHandler()));

        await Assert.ThrowsAsync<ArgumentNullException>(() => transport.SendAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_EndpointWithUserInfoRejectedWithoutLeakingSecrets()
    {
        const string endpoint = "http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443";
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(new FakeHandler()));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendAsync(Request().WithEndpoint(endpoint), CancellationToken.None));

        AssertDoesNotLeak(ex.Message, endpoint, "rpc-user", "SUPER_SECRET_PASSWORD");
    }

    [Theory]
    [InlineData("Endpoint")]
    [InlineData("Username")]
    [InlineData("Password")]
    public async Task SendAsync_MissingRequiredSecretBearingFieldsRejectedSafely(string fieldName)
    {
        var request = Request();
        request = fieldName switch
        {
            "Endpoint" => request.WithEndpoint(" "),
            "Username" => request.WithUsername(" "),
            "Password" => request.WithPassword(" "),
            _ => request
        };
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(new FakeHandler()));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendAsync(request, CancellationToken.None));

        AssertDoesNotLeak(ex.Message, "http://127.0.0.1:18443", "rpc-user", "rpc-password");
    }

    [Fact]
    public async Task SendAsync_NonPositiveTimeoutRejectedSafely()
    {
        var request = new BitcoinJsonRpcTransportRequest
        {
            Endpoint = "http://127.0.0.1:18443",
            Username = "rpc-user",
            Password = "rpc-password",
            Method = "sendmany",
            ParamsJson = """["",{"addr-a":1.25}]""",
            RequestId = "1",
            RequestTimeoutSeconds = 0
        };
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(new FakeHandler()));

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            transport.SendAsync(request, CancellationToken.None));

        AssertDoesNotLeak(ex.Message, "http://127.0.0.1:18443", "rpc-user", "rpc-password");
    }

    [Fact]
    public async Task SendAsync_PostsJsonRpcWithBasicAuth()
    {
        var handler = new FakeHandler();
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));

        await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("application/json", handler.LastContentType);
        Assert.Equal("Basic", handler.LastAuthorizationScheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("rpc-user:rpc-password")),
            handler.LastAuthorizationParameter);
        using var document = JsonDocument.Parse(handler.LastRequestBody);
        var root = document.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("sendmany", root.GetProperty("method").GetString());
        Assert.Equal("1", root.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("params").ValueKind);
        Assert.Equal(1.25m, root.GetProperty("params")[1].GetProperty("addr-a").GetDecimal());
    }

    [Fact]
    public async Task SendAsync_NonWalletEndpointPostsToEndpoint()
    {
        var handler = new FakeHandler();
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));

        await transport.SendAsync(Request().WithEndpoint("http://127.0.0.1:18443/rpc"), CancellationToken.None);

        Assert.Equal("http://127.0.0.1:18443/rpc", handler.LastRequestUri.ToString());
    }

    [Fact]
    public async Task SendAsync_WalletNameAppendsEscapedWalletPath()
    {
        var handler = new FakeHandler();
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));

        await transport.SendAsync(Request().WithWalletName("pool/a wallet"), CancellationToken.None);

        Assert.Equal("http://127.0.0.1:18443/wallet/pool%2Fa%20wallet",
            handler.LastRequestUri.AbsoluteUri);
        Assert.Equal(2, handler.LastRequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task SendAsync_ResponsesAreReturned()
    {
        var handler = new FakeHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"result":"txid","error":null,"id":"1"}""")
            }
        };
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));

        var response = await transport.SendAsync(Request(), CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("""{"result":"txid","error":null,"id":"1"}""", response.Body);
    }

    [Fact]
    public async Task SendAsync_NonSuccessJsonRpcErrorBodyIsReturnedNotThrown()
    {
        var handler = new FakeHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"result":null,"error":{"code":-5},"id":"1"}""")
            }
        };
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));

        var response = await transport.SendAsync(Request(), CancellationToken.None);

        Assert.False(response.IsSuccess);
        Assert.Equal(400, response.StatusCode);
        Assert.Contains(@"""error""", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_NonSuccessEmptyBodyIsReturnedNotThrown()
    {
        var handler = new FakeHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(string.Empty)
            }
        };
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));

        var response = await transport.SendAsync(Request(), CancellationToken.None);

        Assert.False(response.IsSuccess);
        Assert.Equal(500, response.StatusCode);
        Assert.Equal(string.Empty, response.Body);
    }

    [Fact]
    public async Task SendAsync_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler
        {
            OnSend = (_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
        };
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task SendAsync_HttpRequestExceptionPropagates()
    {
        var handler = new FakeHandler
        {
            OnSend = (_, _) => throw new HttpRequestException("transport failed")
        };
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            transport.SendAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_TaskCanceledExceptionPropagatesWithoutLeakingSecrets()
    {
        var handler = new FakeHandler
        {
            OnSend = (_, _) => throw new TaskCanceledException("transport timeout")
        };
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(Request(), CancellationToken.None));

        AssertDoesNotLeak(ex.Message, "rpc-user", "rpc-password", "http://127.0.0.1:18443");
    }

    [Fact]
    public void ToStringDoesNotExposeSecrets()
    {
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(new FakeHandler()));

        var summary = transport.ToString();

        AssertDoesNotLeak(summary, "rpc-user", "rpc-password", "http://127.0.0.1:18443", "Authorization");
    }

    [Fact]
    public void RequestSafeSummaryDoesNotExposeSecrets()
    {
        const string walletName = "secret-wallet";
        var request = Request()
            .WithEndpoint("http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443")
            .WithWalletName(walletName);

        var summary = request.ToSafeSummary();

        AssertDoesNotLeak(summary, "rpc-user", "rpc-password", "SUPER_SECRET_PASSWORD", walletName,
            "http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443", "Authorization");
        Assert.Contains("WalletNameSet=True", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonRpcPayoutClientSendManyUsesHttpTransport()
    {
        var handler = new FakeHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"result":"txid-sendmany","error":null,"id":"1"}""")
            }
        };
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        var result = await client.SendManyAsync(new BitcoinPayoutSendManyRequest
        {
            PoolId = "pool-a",
            Coin = "bitcoin",
            BatchId = 10,
            AttemptId = 20,
            Method = "sendmany",
            Recipients = new Dictionary<string, decimal> { ["addr-a"] = 1m }
        }, CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal("txid-sendmany", result.TxId);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("Basic", handler.LastAuthorizationScheme);
    }

    [Fact]
    public async Task JsonRpcPayoutClientSendToAddressUsesHttpTransport()
    {
        var handler = new FakeHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"result":"txid-sendtoaddress","error":null,"id":"1"}""")
            }
        };
        var transport = new BitcoinJsonRpcHttpTransport(HttpClientWith(handler));
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        var result = await client.SendToAddressAsync(new BitcoinPayoutSendToAddressRequest
        {
            PoolId = "pool-a",
            Coin = "bitcoin",
            BatchId = 10,
            AttemptId = 20,
            Method = "sendtoaddress",
            Address = "addr-a",
            Amount = 1m
        }, CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal("txid-sendtoaddress", result.TxId);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        using var document = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal("sendtoaddress", document.RootElement.GetProperty("method").GetString());
    }

    private static HttpClient HttpClientWith(HttpMessageHandler handler)
    {
        return new HttpClient(handler);
    }

    private static BitcoinJsonRpcTransportRequest Request()
    {
        return new BitcoinJsonRpcTransportRequest
        {
            Endpoint = "http://127.0.0.1:18443",
            Username = "rpc-user",
            Password = "rpc-password",
            Method = "sendmany",
            ParamsJson = """["",{"addr-a":1.25}]""",
            RequestId = "1",
            RequestTimeoutSeconds = 30
        };
    }

    private static BitcoinJsonRpcRouteOptions Options()
    {
        return new BitcoinJsonRpcRouteOptions
        {
            Endpoint = "http://127.0.0.1:18443",
            Username = "rpc-user",
            Password = "rpc-password",
            RequestTimeoutSeconds = 30
        };
    }

    private static void AssertDoesNotLeak(string value, params string[] secrets)
    {
        Assert.All(secrets.Where(x => !string.IsNullOrEmpty(x)), secret =>
            Assert.DoesNotContain(secret, value, StringComparison.Ordinal));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; init; } = new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"result":"txid","error":null,"id":"1"}""")
        };
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> OnSend { get; init; }
        public HttpRequestMessage LastRequest { get; private set; }
        public HttpMethod LastMethod { get; private set; }
        public Uri LastRequestUri { get; private set; }
        public string LastContentType { get; private set; }
        public string LastAuthorizationScheme { get; private set; }
        public string LastAuthorizationParameter { get; private set; }
        public string LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            LastRequestBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return OnSend == null ? Response : OnSend(request, cancellationToken);
        }
    }
}

internal static class BitcoinJsonRpcHttpTransportTestExtensions
{
    public static BitcoinJsonRpcTransportRequest WithEndpoint(
        this BitcoinJsonRpcTransportRequest request,
        string endpoint)
    {
        return Copy(request, endpoint: endpoint);
    }

    public static BitcoinJsonRpcTransportRequest WithUsername(
        this BitcoinJsonRpcTransportRequest request,
        string username)
    {
        return Copy(request, username: username);
    }

    public static BitcoinJsonRpcTransportRequest WithPassword(
        this BitcoinJsonRpcTransportRequest request,
        string password)
    {
        return Copy(request, password: password);
    }

    public static BitcoinJsonRpcTransportRequest WithWalletName(
        this BitcoinJsonRpcTransportRequest request,
        string walletName)
    {
        return Copy(request, walletName: walletName);
    }

    private static BitcoinJsonRpcTransportRequest Copy(
        BitcoinJsonRpcTransportRequest request,
        string endpoint = null,
        string username = null,
        string password = null,
        string walletName = null)
    {
        return new BitcoinJsonRpcTransportRequest
        {
            Endpoint = endpoint ?? request.Endpoint,
            Username = username ?? request.Username,
            Password = password ?? request.Password,
            WalletName = walletName ?? request.WalletName,
            Method = request.Method,
            ParamsJson = request.ParamsJson,
            RequestId = request.RequestId,
            RequestTimeoutSeconds = request.RequestTimeoutSeconds
        };
    }
}
