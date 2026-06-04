using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Payouts.Bitcoin;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class BitcoinJsonRpcPayoutClientTests
{
    [Fact]
    public void Constructor_NullTransportThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new BitcoinJsonRpcPayoutClient(null, Options()));
    }

    [Fact]
    public void Constructor_NullOptionsThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new BitcoinJsonRpcPayoutClient(new FakeTransport(), null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_MissingEndpointThrows(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new BitcoinJsonRpcPayoutClient(new FakeTransport(),
            Options().WithEndpoint(endpoint)));
    }

    [Fact]
    public void Constructor_InvalidOptionsThrow()
    {
        Assert.Throws<ArgumentException>(() => new BitcoinJsonRpcPayoutClient(new FakeTransport(),
            Options().WithUsername(" ")));
        Assert.Throws<ArgumentException>(() => new BitcoinJsonRpcPayoutClient(new FakeTransport(),
            Options().WithPassword(" ")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitcoinJsonRpcPayoutClient(new FakeTransport(),
            Options().WithTimeout(0)));
        Assert.Throws<ArgumentException>(() => new BitcoinJsonRpcPayoutClient(new FakeTransport(),
            Options().WithEndpoint("not an absolute uri")));
    }

    [Fact]
    public void Constructor_InvalidWalletUnlockOptionsThrowWithoutLeakingPassphrase()
    {
        const string passphrase = "SUPER_SECRET_WALLET_PASSPHRASE";

        var missingUnlockSeconds = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BitcoinJsonRpcPayoutClient(new FakeTransport(), Options(walletPassphrase: passphrase)));
        var missingPassphrase = Assert.Throws<ArgumentException>(() =>
            new BitcoinJsonRpcPayoutClient(new FakeTransport(), Options(walletUnlockSeconds: 30)));
        var tooLarge = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BitcoinJsonRpcPayoutClient(new FakeTransport(),
                Options(walletPassphrase: passphrase, walletUnlockSeconds: 3601)));

        Assert.DoesNotContain(passphrase, missingUnlockSeconds.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(passphrase, missingPassphrase.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(passphrase, tooLarge.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_EndpointWithUserInfoThrows()
    {
        const string endpoint = "http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443";

        var ex = Assert.Throws<ArgumentException>(() => new BitcoinJsonRpcPayoutClient(new FakeTransport(),
            Options().WithEndpoint(endpoint)));

        Assert.DoesNotContain(endpoint, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("rpc-user", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPER_SECRET_PASSWORD", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsToStringDoesNotLeakSecrets()
    {
        const string endpoint = "http://127.0.0.1:18443";
        const string username = "rpc-user";
        const string password = "SUPER_SECRET_PASSWORD";
        const string walletName = "secret-wallet";
        const string walletPassphrase = "SUPER_SECRET_WALLET_PASSPHRASE";
        var options = new BitcoinJsonRpcRouteOptions
        {
            Endpoint = endpoint,
            Username = username,
            Password = password,
            WalletName = walletName,
            WalletPassphrase = walletPassphrase,
            WalletUnlockSeconds = 60
        };

        var summary = options.ToString();

        Assert.DoesNotContain(endpoint, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(username, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(password, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(walletName, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(walletPassphrase, summary, StringComparison.Ordinal);
        Assert.Contains("EndpointSet=True", summary, StringComparison.Ordinal);
        Assert.Contains("UsernameSet=True", summary, StringComparison.Ordinal);
        Assert.Contains("WalletNameSet=True", summary, StringComparison.Ordinal);
        Assert.Contains("WalletPassphraseSet=True", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendManyAsync_ValidRequestCallsTransportOnceWithSendManyParams()
    {
        var transport = new FakeTransport();
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        var result = await client.SendManyAsync(SendManyRequest(new Dictionary<string, decimal>
        {
            ["addr-a"] = 1.25m,
            ["addr-b"] = 2m
        }), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal("sendmany", transport.LastRequest.Method);
        Assert.Equal("http://127.0.0.1:18443", transport.LastRequest.Endpoint);
        Assert.Equal("rpc-user", transport.LastRequest.Username);
        Assert.Equal("rpc-password", transport.LastRequest.Password);
        Assert.Equal(30, transport.LastRequest.RequestTimeoutSeconds);
        Assert.False(string.IsNullOrWhiteSpace(transport.LastRequest.RequestId));
        using var document = JsonDocument.Parse(transport.LastRequest.ParamsJson);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal("", root[0].GetString());
        Assert.Equal(JsonValueKind.Object, root[1].ValueKind);
        Assert.Equal(JsonValueKind.Number, root[1].GetProperty("addr-a").ValueKind);
        Assert.Equal(1.25m, root[1].GetProperty("addr-a").GetDecimal());
        Assert.Equal(2m, root[1].GetProperty("addr-b").GetDecimal());
    }

    [Fact]
    public async Task SendManyAsync_AcceptedResultStringMapsToAcceptedTxId()
    {
        var client = new BitcoinJsonRpcPayoutClient(new FakeTransport
        {
            Response = Response("""{"result":"txid-sendmany","error":null,"id":"1"}""")
        }, Options());

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal("txid-sendmany", result.TxId);
    }

    [Fact]
    public async Task SendManyAsync_JsonRpcErrorMapsToFailedPreAccept()
    {
        var client = new BitcoinJsonRpcPayoutClient(new FakeTransport
        {
            Response = Response("""{"result":null,"error":{"code":-5,"message":"bad address"},"id":"1"}""")
        }, Options());

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.JsonRpcErrorCode, result.ErrorCode);
        Assert.Contains("-5", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendManyAsync_WalletLockedWithoutPassphraseFailsPreAcceptWithoutUnlock()
    {
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.WalletLockedErrorCode, result.ErrorCode);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal("sendmany", transport.Requests[0].Method);
    }

    [Fact]
    public async Task SendToAddressAsync_WalletLockedWithoutPassphraseFailsPreAcceptWithoutUnlock()
    {
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        var result = await client.SendToAddressAsync(SendToAddressRequest("addr-a", 1m),
            CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.WalletLockedErrorCode, result.ErrorCode);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal("sendtoaddress", transport.Requests[0].Method);
    }

    [Fact]
    public async Task SendManyAsync_WalletLockedWithPassphraseUnlocksRetriesAndLocks()
    {
        const string passphrase = "SUPER_SECRET_WALLET_PASSPHRASE";
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        transport.Responses.Enqueue(Response("""{"result":null,"error":null,"id":"2"}"""));
        transport.Responses.Enqueue(Response("""{"result":"txid-after-unlock","error":null,"id":"3"}"""));
        transport.Responses.Enqueue(Response("""{"result":null,"error":null,"id":"4"}"""));
        var client = new BitcoinJsonRpcPayoutClient(transport,
            Options(walletPassphrase: passphrase, walletUnlockSeconds: 45));

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal("txid-after-unlock", result.TxId);
        Assert.Equal(new[] { "sendmany", "walletpassphrase", "sendmany", "walletlock" },
            transport.Requests.Select(x => x.Method).ToArray());

        using var document = JsonDocument.Parse(transport.Requests[1].ParamsJson);
        Assert.Equal(passphrase, document.RootElement[0].GetString());
        Assert.Equal(45, document.RootElement[1].GetInt32());
        Assert.DoesNotContain(passphrase, transport.Requests[1].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Requests[1].ParamsJson, transport.Requests[1].ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendToAddressAsync_WalletLockedWithPassphraseUnlocksRetriesAndAccepts()
    {
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        transport.Responses.Enqueue(Response("""{"result":null,"error":null,"id":"2"}"""));
        transport.Responses.Enqueue(Response("""{"result":"txid-after-unlock","error":null,"id":"3"}"""));
        transport.Responses.Enqueue(Response("""{"result":null,"error":null,"id":"4"}"""));
        var client = new BitcoinJsonRpcPayoutClient(transport,
            Options(walletPassphrase: "wallet-passphrase", walletUnlockSeconds: 60));

        var result = await client.SendToAddressAsync(SendToAddressRequest("addr-a", 1m),
            CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal("txid-after-unlock", result.TxId);
        Assert.Equal(new[] { "sendtoaddress", "walletpassphrase", "sendtoaddress", "walletlock" },
            transport.Requests.Select(x => x.Method).ToArray());
    }

    [Fact]
    public async Task SendManyAsync_WalletLockIsSkippedWhenDisabled()
    {
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        transport.Responses.Enqueue(Response("""{"result":null,"error":null,"id":"2"}"""));
        transport.Responses.Enqueue(Response("""{"result":"txid-after-unlock","error":null,"id":"3"}"""));
        var client = new BitcoinJsonRpcPayoutClient(transport,
            Options(walletPassphrase: "wallet-passphrase", walletUnlockSeconds: 30,
                lockWalletAfterSend: false));

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal(new[] { "sendmany", "walletpassphrase", "sendmany" },
            transport.Requests.Select(x => x.Method).ToArray());
    }

    [Fact]
    public async Task SendManyAsync_WalletLockFailureAfterAcceptedTxIdStillReturnsAccepted()
    {
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        transport.Responses.Enqueue(Response("""{"result":null,"error":null,"id":"2"}"""));
        transport.Responses.Enqueue(Response("""{"result":"txid-after-unlock","error":null,"id":"3"}"""));
        transport.Responses.Enqueue(new InvalidOperationException("wallet lock failed SECRET"));
        var client = new BitcoinJsonRpcPayoutClient(transport,
            Options(walletPassphrase: "wallet-passphrase", walletUnlockSeconds: 30));

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal("txid-after-unlock", result.TxId);
        Assert.Equal(4, transport.CallCount);
    }

    [Fact]
    public async Task SendManyAsync_WalletLockFailureAfterRetryErrorPreservesRetryResult()
    {
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        transport.Responses.Enqueue(Response("""{"result":null,"error":null,"id":"2"}"""));
        transport.Responses.Enqueue(Response("""{"result":null,"error":{"code":-6,"message":"insufficient funds"},"id":"3"}"""));
        transport.Responses.Enqueue(new InvalidOperationException("wallet lock failed SECRET"));
        var client = new BitcoinJsonRpcPayoutClient(transport,
            Options(walletPassphrase: "wallet-passphrase", walletUnlockSeconds: 30));

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.JsonRpcErrorCode, result.ErrorCode);
        Assert.Contains("-6", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(4, transport.CallCount);
    }

    [Fact]
    public async Task SendManyAsync_WalletPassphraseJsonRpcErrorFailsPreAccept()
    {
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        transport.Responses.Enqueue(Response("""{"result":null,"error":{"code":-14,"message":"bad passphrase"},"id":"2"}"""));
        var client = new BitcoinJsonRpcPayoutClient(transport,
            Options(walletPassphrase: "wallet-passphrase", walletUnlockSeconds: 30));

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.WalletUnlockFailedErrorCode, result.ErrorCode);
        Assert.Equal(2, transport.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("""{"result":"unexpected","error":null,"id":"2"}""")]
    public async Task SendManyAsync_WalletPassphraseAmbiguousResponseMarksAmbiguous(string unlockBody)
    {
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        transport.Responses.Enqueue(Response(unlockBody));
        var client = new BitcoinJsonRpcPayoutClient(transport,
            Options(walletPassphrase: "wallet-passphrase", walletUnlockSeconds: 30));

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.AmbiguousRequiresReview, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.WalletUnlockAmbiguousErrorCode, result.ErrorCode);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task SendManyAsync_WalletPassphraseTransportExceptionMarksAmbiguous()
    {
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        transport.Responses.Enqueue(new InvalidOperationException("unlock failed SECRET"));
        var client = new BitcoinJsonRpcPayoutClient(transport,
            Options(walletPassphrase: "wallet-passphrase", walletUnlockSeconds: 30));

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.AmbiguousRequiresReview, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.WalletUnlockAmbiguousErrorCode, result.ErrorCode);
        Assert.Equal(2, transport.CallCount);
        Assert.DoesNotContain("SECRET", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendManyAsync_RepeatedWalletLockedAfterRetryDoesNotRetryAgain()
    {
        var transport = new FakeTransport();
        transport.Responses.Enqueue(WalletLockedResponse());
        transport.Responses.Enqueue(Response("""{"result":null,"error":null,"id":"2"}"""));
        transport.Responses.Enqueue(WalletLockedResponse());
        transport.Responses.Enqueue(Response("""{"result":null,"error":null,"id":"4"}"""));
        var client = new BitcoinJsonRpcPayoutClient(transport,
            Options(walletPassphrase: "wallet-passphrase", walletUnlockSeconds: 30));

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.WalletLockedErrorCode, result.ErrorCode);
        Assert.Equal(new[] { "sendmany", "walletpassphrase", "sendmany", "walletlock" },
            transport.Requests.Select(x => x.Method).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task SendManyAsync_InvalidRecipientFailsPreAcceptWithoutTransportCall(string address)
    {
        var transport = new FakeTransport();
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        var result = await client.SendManyAsync(SendManyRequest(new Dictionary<string, decimal>
        {
            [address] = 1m
        }), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.InvalidRecipientErrorCode, result.ErrorCode);
        Assert.Equal(0, transport.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SendManyAsync_InvalidAmountFailsPreAcceptWithoutTransportCall(decimal amount)
    {
        var transport = new FakeTransport();
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        var result = await client.SendManyAsync(SendManyRequest(new Dictionary<string, decimal>
        {
            ["addr-a"] = amount
        }), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.InvalidAmountErrorCode, result.ErrorCode);
        Assert.Equal(0, transport.CallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not json")]
    [InlineData("""{"result":123,"error":null,"id":"1"}""")]
    [InlineData("""{"error":null,"id":"1"}""")]
    [InlineData("""{"result":"txid","error":{"code":-5},"id":"1"}""")]
    public async Task SendManyAsync_InvalidResponseMapsAmbiguous(string body)
    {
        var client = new BitcoinJsonRpcPayoutClient(new FakeTransport
        {
            Response = Response(body)
        }, Options());

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.AmbiguousRequiresReview, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.InvalidResponseErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task SendManyAsync_HttpFailureWithoutParseableJsonRpcErrorMapsAmbiguous()
    {
        var client = new BitcoinJsonRpcPayoutClient(new FakeTransport
        {
            Response = new BitcoinJsonRpcTransportResponse
            {
                IsSuccess = false,
                StatusCode = 500,
                Body = "server unavailable"
            }
        }, Options());

        var result = await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.AmbiguousRequiresReview, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.InvalidResponseErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task SendToAddressAsync_ValidRequestCallsTransportOnceWithParams()
    {
        var transport = new FakeTransport();
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        var result = await client.SendToAddressAsync(SendToAddressRequest("addr-a", 1.5m),
            CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal("sendtoaddress", transport.LastRequest.Method);
        using var document = JsonDocument.Parse(transport.LastRequest.ParamsJson);
        var root = document.RootElement;
        Assert.Equal("addr-a", root[0].GetString());
        Assert.Equal(JsonValueKind.Number, root[1].ValueKind);
        Assert.Equal(1.5m, root[1].GetDecimal());
    }

    [Fact]
    public async Task SendToAddressAsync_AcceptedResultStringMapsToAcceptedTxId()
    {
        var client = new BitcoinJsonRpcPayoutClient(new FakeTransport
        {
            Response = Response("""{"result":"txid-sendtoaddress","error":null,"id":"1"}""")
        }, Options());

        var result = await client.SendToAddressAsync(SendToAddressRequest("addr-a", 1m),
            CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal("txid-sendtoaddress", result.TxId);
    }

    [Fact]
    public async Task SendToAddressAsync_JsonRpcErrorMapsToFailedPreAccept()
    {
        var client = new BitcoinJsonRpcPayoutClient(new FakeTransport
        {
            Response = Response("""{"result":null,"error":{"code":-6,"message":"too small"},"id":"1"}""")
        }, Options());

        var result = await client.SendToAddressAsync(SendToAddressRequest("addr-a", 1m),
            CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinJsonRpcPayoutClient.JsonRpcErrorCode, result.ErrorCode);
        Assert.Contains("-6", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData(" ", 1)]
    [InlineData("addr-a", 0)]
    [InlineData("addr-a", -1)]
    public async Task SendToAddressAsync_InvalidRecipientOrAmountFailsPreAcceptWithoutTransportCall(
        string address,
        decimal amount)
    {
        var transport = new FakeTransport();
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        var result = await client.SendToAddressAsync(SendToAddressRequest(address, amount),
            CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task SendManyAsync_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var transport = new FakeTransport
        {
            OnSend = (_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
        };
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendManyAsync(SendManyRequest(), cts.Token));
    }

    [Fact]
    public async Task SendManyAsync_UnexpectedTransportExceptionPropagates()
    {
        var transport = new FakeTransport
        {
            OnSend = (_, _) => throw new InvalidOperationException("transport failed")
        };
        var client = new BitcoinJsonRpcPayoutClient(transport, Options());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendManyAsync(SendManyRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task TransportRequestAndResponseSummariesDoNotLeakSecrets()
    {
        const string endpoint = "http://127.0.0.1:18443";
        const string username = "rpc-user";
        const string password = "SUPER_SECRET_PASSWORD";
        var transport = new FakeTransport();
        var client = new BitcoinJsonRpcPayoutClient(transport, Options(endpoint, username, password));

        await client.SendManyAsync(SendManyRequest(), CancellationToken.None);

        var requestSummary = transport.LastRequest.ToString();
        Assert.DoesNotContain(endpoint, requestSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(username, requestSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(password, requestSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-wallet", requestSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(transport.LastRequest.ParamsJson, requestSummary, StringComparison.Ordinal);
        Assert.Contains("EndpointSet=True", requestSummary, StringComparison.Ordinal);
        Assert.Contains("WalletNameSet=False", requestSummary, StringComparison.Ordinal);

        var responseSummary = transport.Response.ToString();
        Assert.DoesNotContain(transport.Response.Body, responseSummary, StringComparison.Ordinal);
        Assert.Contains("BodySet=True", responseSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoutingClientRoutesJsonRpcClientsByPoolAndCoin()
    {
        var transportA = new FakeTransport
        {
            Response = Response("""{"result":"txid-pool-a","error":null,"id":"1"}""")
        };
        var transportB = new FakeTransport
        {
            Response = Response("""{"result":"txid-pool-b","error":null,"id":"1"}""")
        };
        var routeA = new BitcoinJsonRpcPayoutClient(transportA, Options());
        var routeB = new BitcoinJsonRpcPayoutClient(transportB, Options(endpoint: "http://127.0.0.1:18444"));
        var routingClient = new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route("pool-a", "bitcoin", routeA),
            Route("pool-b", "bitcoin", routeB)
        });

        var resultA = await routingClient.SendManyAsync(SendManyRequest(poolId: "pool-a"), CancellationToken.None);
        var resultB = await routingClient.SendManyAsync(SendManyRequest(poolId: "pool-b"), CancellationToken.None);

        Assert.Equal("txid-pool-a", resultA.TxId);
        Assert.Equal("txid-pool-b", resultB.TxId);
        Assert.Equal(1, transportA.CallCount);
        Assert.Equal(1, transportB.CallCount);
    }

    private static BitcoinJsonRpcRouteOptions Options(
        string endpoint = "http://127.0.0.1:18443",
        string username = "rpc-user",
        string password = "rpc-password",
        string walletPassphrase = "",
        int walletUnlockSeconds = 0,
        bool lockWalletAfterSend = true)
    {
        return new BitcoinJsonRpcRouteOptions
        {
            Endpoint = endpoint,
            Username = username,
            Password = password,
            WalletPassphrase = walletPassphrase,
            WalletUnlockSeconds = walletUnlockSeconds,
            LockWalletAfterSend = lockWalletAfterSend,
            RequestTimeoutSeconds = 30
        };
    }

    private static BitcoinPayoutSendManyRequest SendManyRequest(
        IReadOnlyDictionary<string, decimal> recipients = null,
        string poolId = "pool-a")
    {
        return new BitcoinPayoutSendManyRequest
        {
            PoolId = poolId,
            Coin = "bitcoin",
            BatchId = 10,
            AttemptId = 20,
            Method = "sendmany",
            Recipients = recipients ?? new Dictionary<string, decimal>
            {
                ["addr-a"] = 1m
            }
        };
    }

    private static BitcoinPayoutSendToAddressRequest SendToAddressRequest(string address, decimal amount)
    {
        return new BitcoinPayoutSendToAddressRequest
        {
            PoolId = "pool-a",
            Coin = "bitcoin",
            BatchId = 10,
            AttemptId = 20,
            Method = "sendtoaddress",
            Address = address,
            Amount = amount
        };
    }

    private static BitcoinJsonRpcTransportResponse Response(string body)
    {
        return new BitcoinJsonRpcTransportResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            Body = body
        };
    }

    private static BitcoinJsonRpcTransportResponse WalletLockedResponse()
    {
        return Response("""{"result":null,"error":{"code":-13,"message":"wallet locked"},"id":"1"}""");
    }

    private static BitcoinPayoutRpcRouteRegistration Route(
        string poolId,
        string coin,
        IBitcoinPayoutRpcClient client)
    {
        return new BitcoinPayoutRpcRouteRegistration
        {
            RouteKey = new BitcoinPayoutRpcRouteKey
            {
                PoolId = poolId,
                Coin = coin
            },
            Client = client,
            AllowSendMany = true,
            AllowSendToAddress = true
        };
    }

    private sealed class FakeTransport : IBitcoinJsonRpcTransport
    {
        public BitcoinJsonRpcTransportResponse Response { get; init; } =
            BitcoinJsonRpcPayoutClientTests.Response("""{"result":"txid-default","error":null,"id":"1"}""");
        public Func<BitcoinJsonRpcTransportRequest, CancellationToken, BitcoinJsonRpcTransportResponse> OnSend { get; init; }
        public Queue<object> Responses { get; } = new();
        public List<BitcoinJsonRpcTransportRequest> Requests { get; } = new();
        public int CallCount { get; private set; }
        public BitcoinJsonRpcTransportRequest LastRequest { get; private set; }

        public Task<BitcoinJsonRpcTransportResponse> SendAsync(BitcoinJsonRpcTransportRequest request,
            CancellationToken ct)
        {
            CallCount++;
            LastRequest = request;
            Requests.Add(request);
            if(Responses.Count > 0)
            {
                var response = Responses.Dequeue();
                if(response is Exception ex)
                    throw ex;

                return Task.FromResult((BitcoinJsonRpcTransportResponse)response);
            }

            return Task.FromResult(OnSend == null ? Response : OnSend(request, ct));
        }
    }
}

internal static class BitcoinJsonRpcRouteOptionsTestExtensions
{
    public static BitcoinJsonRpcRouteOptions WithEndpoint(this BitcoinJsonRpcRouteOptions options, string endpoint)
    {
        return new BitcoinJsonRpcRouteOptions
        {
            Endpoint = endpoint,
            Username = options.Username,
            Password = options.Password,
            WalletName = options.WalletName,
            WalletPassphrase = options.WalletPassphrase,
            WalletUnlockSeconds = options.WalletUnlockSeconds,
            LockWalletAfterSend = options.LockWalletAfterSend,
            RequestTimeoutSeconds = options.RequestTimeoutSeconds
        };
    }

    public static BitcoinJsonRpcRouteOptions WithUsername(this BitcoinJsonRpcRouteOptions options, string username)
    {
        return new BitcoinJsonRpcRouteOptions
        {
            Endpoint = options.Endpoint,
            Username = username,
            Password = options.Password,
            WalletName = options.WalletName,
            WalletPassphrase = options.WalletPassphrase,
            WalletUnlockSeconds = options.WalletUnlockSeconds,
            LockWalletAfterSend = options.LockWalletAfterSend,
            RequestTimeoutSeconds = options.RequestTimeoutSeconds
        };
    }

    public static BitcoinJsonRpcRouteOptions WithPassword(this BitcoinJsonRpcRouteOptions options, string password)
    {
        return new BitcoinJsonRpcRouteOptions
        {
            Endpoint = options.Endpoint,
            Username = options.Username,
            Password = password,
            WalletName = options.WalletName,
            WalletPassphrase = options.WalletPassphrase,
            WalletUnlockSeconds = options.WalletUnlockSeconds,
            LockWalletAfterSend = options.LockWalletAfterSend,
            RequestTimeoutSeconds = options.RequestTimeoutSeconds
        };
    }

    public static BitcoinJsonRpcRouteOptions WithTimeout(this BitcoinJsonRpcRouteOptions options, int timeout)
    {
        return new BitcoinJsonRpcRouteOptions
        {
            Endpoint = options.Endpoint,
            Username = options.Username,
            Password = options.Password,
            WalletName = options.WalletName,
            WalletPassphrase = options.WalletPassphrase,
            WalletUnlockSeconds = options.WalletUnlockSeconds,
            LockWalletAfterSend = options.LockWalletAfterSend,
            RequestTimeoutSeconds = timeout
        };
    }
}
