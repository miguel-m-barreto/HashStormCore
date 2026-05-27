using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Payouts.Bitcoin;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class BitcoinPayoutRpcRoutingClientTests
{
    [Fact]
    public void Constructor_NullRoutesThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new BitcoinPayoutRpcRoutingClient(null));
    }

    [Fact]
    public void Constructor_DuplicateRouteKeyThrows()
    {
        var key = RouteKey("pool-a", "bitcoin");

        Assert.Throws<ArgumentException>(() => new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(key, new FakeBitcoinPayoutRpcClient()),
            Route(key, new FakeBitcoinPayoutRpcClient())
        }));
    }

    [Theory]
    [InlineData("", "bitcoin")]
    [InlineData(" ", "bitcoin")]
    [InlineData("pool-a", "")]
    [InlineData("pool-a", " ")]
    public void Constructor_MissingRoutePoolIdOrCoinThrows(string poolId, string coin)
    {
        Assert.Throws<ArgumentException>(() => new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey(poolId, coin), new FakeBitcoinPayoutRpcClient())
        }));
    }

    [Fact]
    public void Constructor_MissingRouteKeyThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(null, new FakeBitcoinPayoutRpcClient())
        }));
    }

    [Fact]
    public void Constructor_MissingRouteClientThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey("pool-a", "bitcoin"), null)
        }));
    }

    [Fact]
    public void Constructor_RouteWithNoAllowedMethodThrows()
    {
        Assert.Throws<ArgumentException>(() => new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey("pool-a", "bitcoin"), new FakeBitcoinPayoutRpcClient(), false, false)
        }));
    }

    [Fact]
    public async Task SendManyAsync_DelegatesToExactPoolAndCoinRoute()
    {
        var poolA = new FakeBitcoinPayoutRpcClient();
        var poolB = new FakeBitcoinPayoutRpcClient();
        var client = new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey("pool-a", "bitcoin"), poolA),
            Route(RouteKey("pool-b", "bitcoin"), poolB)
        });

        var result = await client.SendManyAsync(SendManyRequest("pool-b", "bitcoin"), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal(0, poolA.SendManyCallCount);
        Assert.Equal(1, poolB.SendManyCallCount);
    }

    [Fact]
    public async Task SendManyAsync_NullRequestThrows()
    {
        var client = new BitcoinPayoutRpcRoutingClient(Array.Empty<BitcoinPayoutRpcRouteRegistration>());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.SendManyAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task SendToAddressAsync_DelegatesToExactPoolAndCoinRoute()
    {
        var bitcoin = new FakeBitcoinPayoutRpcClient();
        var litecoin = new FakeBitcoinPayoutRpcClient();
        var client = new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey("pool-a", "bitcoin"), bitcoin),
            Route(RouteKey("pool-a", "litecoin"), litecoin)
        });

        var result = await client.SendToAddressAsync(SendToAddressRequest("pool-a", "litecoin"),
            CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.Accepted, result.Status);
        Assert.Equal(0, bitcoin.SendToAddressCallCount);
        Assert.Equal(1, litecoin.SendToAddressCallCount);
    }

    [Fact]
    public async Task SendToAddressAsync_NullRequestThrows()
    {
        var client = new BitcoinPayoutRpcRoutingClient(Array.Empty<BitcoinPayoutRpcRouteRegistration>());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.SendToAddressAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task SendManyAsync_RequestForAnotherPoolDoesNotCallWrongRoute()
    {
        var routeClient = new FakeBitcoinPayoutRpcClient();
        var client = new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey("pool-a", "bitcoin"), routeClient)
        });

        var result = await client.SendManyAsync(SendManyRequest("pool-b", "bitcoin"), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinPayoutRpcRoutingClient.RouteNotConfiguredErrorCode, result.ErrorCode);
        Assert.Equal(0, routeClient.TotalCallCount);
    }

    [Fact]
    public async Task SendManyAsync_RequestForAnotherCoinDoesNotCallWrongRoute()
    {
        var routeClient = new FakeBitcoinPayoutRpcClient();
        var client = new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey("pool-a", "bitcoin"), routeClient)
        });

        var result = await client.SendManyAsync(SendManyRequest("pool-a", "litecoin"), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinPayoutRpcRoutingClient.RouteNotConfiguredErrorCode, result.ErrorCode);
        Assert.Equal(0, routeClient.TotalCallCount);
    }

    [Fact]
    public async Task SendManyAsync_MissingRouteReturnsFailedPreAccept()
    {
        var client = new BitcoinPayoutRpcRoutingClient(Array.Empty<BitcoinPayoutRpcRouteRegistration>());

        var result = await client.SendManyAsync(SendManyRequest("pool-a", "bitcoin"), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinPayoutRpcRoutingClient.RouteNotConfiguredErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task SendManyAsync_DisabledRouteReturnsFailedPreAcceptWithoutClientCall()
    {
        var routeClient = new FakeBitcoinPayoutRpcClient();
        var client = new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey("pool-a", "bitcoin"), routeClient, allowSendMany: false)
        });

        var result = await client.SendManyAsync(SendManyRequest("pool-a", "bitcoin"), CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinPayoutRpcRoutingClient.SendManyDisabledErrorCode, result.ErrorCode);
        Assert.Equal(0, routeClient.TotalCallCount);
    }

    [Fact]
    public async Task SendToAddressAsync_DisabledRouteReturnsFailedPreAcceptWithoutClientCall()
    {
        var routeClient = new FakeBitcoinPayoutRpcClient();
        var client = new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey("pool-a", "bitcoin"), routeClient, allowSendToAddress: false)
        });

        var result = await client.SendToAddressAsync(SendToAddressRequest("pool-a", "bitcoin"),
            CancellationToken.None);

        Assert.Equal(BitcoinPayoutRpcStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinPayoutRpcRoutingClient.SendToAddressDisabledErrorCode, result.ErrorCode);
        Assert.Equal(0, routeClient.TotalCallCount);
    }

    [Fact]
    public async Task SendManyAsync_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var routeClient = new FakeBitcoinPayoutRpcClient
        {
            OnSendMany = (_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
        };
        var client = new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey("pool-a", "bitcoin"), routeClient)
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendManyAsync(SendManyRequest("pool-a", "bitcoin"), cts.Token));
    }

    [Fact]
    public async Task SendManyAsync_UnexpectedRouteClientExceptionPropagates()
    {
        var routeClient = new FakeBitcoinPayoutRpcClient
        {
            OnSendMany = (_, _) => throw new InvalidOperationException("transport failed")
        };
        var client = new BitcoinPayoutRpcRoutingClient(new[]
        {
            Route(RouteKey("pool-a", "bitcoin"), routeClient)
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendManyAsync(SendManyRequest("pool-a", "bitcoin"), CancellationToken.None));
    }

    private static BitcoinPayoutRpcRouteKey RouteKey(string poolId, string coin)
    {
        return new BitcoinPayoutRpcRouteKey
        {
            PoolId = poolId,
            Coin = coin
        };
    }

    private static BitcoinPayoutRpcRouteRegistration Route(
        BitcoinPayoutRpcRouteKey key,
        IBitcoinPayoutRpcClient client,
        bool allowSendMany = true,
        bool allowSendToAddress = true)
    {
        return new BitcoinPayoutRpcRouteRegistration
        {
            RouteKey = key,
            Client = client,
            AllowSendMany = allowSendMany,
            AllowSendToAddress = allowSendToAddress,
            SafeSummary = "EndpointSet=True; UsernameSet=True"
        };
    }

    private static BitcoinPayoutSendManyRequest SendManyRequest(string poolId, string coin)
    {
        return new BitcoinPayoutSendManyRequest
        {
            PoolId = poolId,
            Coin = coin,
            BatchId = 10,
            AttemptId = 20,
            Method = "sendmany",
            Recipients = new Dictionary<string, decimal>
            {
                ["addr-a"] = 1m
            }
        };
    }

    private static BitcoinPayoutSendToAddressRequest SendToAddressRequest(string poolId, string coin)
    {
        return new BitcoinPayoutSendToAddressRequest
        {
            PoolId = poolId,
            Coin = coin,
            BatchId = 10,
            AttemptId = 20,
            Method = "sendtoaddress",
            Address = "addr-a",
            Amount = 1m
        };
    }

    private sealed class FakeBitcoinPayoutRpcClient : IBitcoinPayoutRpcClient
    {
        public Func<BitcoinPayoutSendManyRequest, CancellationToken, BitcoinPayoutRpcResult> OnSendMany { get; init; }
        public int SendManyCallCount { get; private set; }
        public int SendToAddressCallCount { get; private set; }
        public int TotalCallCount => SendManyCallCount + SendToAddressCallCount;

        public Task<BitcoinPayoutRpcResult> SendManyAsync(BitcoinPayoutSendManyRequest request, CancellationToken ct)
        {
            SendManyCallCount++;
            return Task.FromResult(OnSendMany == null
                ? BitcoinPayoutRpcResult.Accepted("txid-sendmany")
                : OnSendMany(request, ct));
        }

        public Task<BitcoinPayoutRpcResult> SendToAddressAsync(BitcoinPayoutSendToAddressRequest request,
            CancellationToken ct)
        {
            SendToAddressCallCount++;
            return Task.FromResult(BitcoinPayoutRpcResult.Accepted("txid-sendtoaddress"));
        }
    }
}
