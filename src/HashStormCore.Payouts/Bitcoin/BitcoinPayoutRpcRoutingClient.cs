namespace HashStormCore.Payouts.Bitcoin;

public record BitcoinPayoutRpcRouteKey
{
    public string PoolId { get; init; } = string.Empty;
    public string Coin { get; init; } = string.Empty;
}

public record BitcoinPayoutRpcRouteRegistration
{
    public BitcoinPayoutRpcRouteKey RouteKey { get; init; }
    public IBitcoinPayoutRpcClient Client { get; init; }
    public bool AllowSendMany { get; init; }
    public bool AllowSendToAddress { get; init; }
    public string SafeSummary { get; init; } = string.Empty;
}

public class BitcoinPayoutRpcRoutingClient : IBitcoinPayoutRpcClient
{
    public const string RouteNotConfiguredErrorCode = "bitcoin_rpc_route_not_configured";
    public const string SendManyDisabledErrorCode = "bitcoin_rpc_route_sendmany_disabled";
    public const string SendToAddressDisabledErrorCode = "bitcoin_rpc_route_sendtoaddress_disabled";

    public BitcoinPayoutRpcRoutingClient(IEnumerable<BitcoinPayoutRpcRouteRegistration> routes)
    {
        if(routes == null)
            throw new ArgumentNullException(nameof(routes));

        foreach(var route in routes)
        {
            if(route == null)
                throw new ArgumentException("Bitcoin RPC route registration cannot be null", nameof(routes));

            ValidateRouteKey(route.RouteKey, nameof(route.RouteKey));

            if(route.Client == null)
                throw new ArgumentNullException(nameof(route.Client));

            if(!route.AllowSendMany && !route.AllowSendToAddress)
                throw new ArgumentException("Bitcoin RPC route registration must allow at least one send method",
                    nameof(routes));

            if(!clients.TryAdd(route.RouteKey, route))
                throw new ArgumentException("Duplicate Bitcoin RPC route registration key", nameof(routes));
        }
    }

    private readonly Dictionary<BitcoinPayoutRpcRouteKey, BitcoinPayoutRpcRouteRegistration> clients = new();

    public Task<BitcoinPayoutRpcResult> SendManyAsync(BitcoinPayoutSendManyRequest request, CancellationToken ct)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(!clients.TryGetValue(RouteKey(request.PoolId, request.Coin), out var route))
            return Task.FromResult(BitcoinPayoutRpcResult.FailedPreAccept(RouteNotConfiguredErrorCode,
                "Bitcoin RPC route is not configured for the payout request"));

        if(!route.AllowSendMany)
            return Task.FromResult(BitcoinPayoutRpcResult.FailedPreAccept(SendManyDisabledErrorCode,
                "Bitcoin RPC route does not allow sendmany"));

        return route.Client.SendManyAsync(request, ct);
    }

    public Task<BitcoinPayoutRpcResult> SendToAddressAsync(BitcoinPayoutSendToAddressRequest request,
        CancellationToken ct)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(!clients.TryGetValue(RouteKey(request.PoolId, request.Coin), out var route))
            return Task.FromResult(BitcoinPayoutRpcResult.FailedPreAccept(RouteNotConfiguredErrorCode,
                "Bitcoin RPC route is not configured for the payout request"));

        if(!route.AllowSendToAddress)
            return Task.FromResult(BitcoinPayoutRpcResult.FailedPreAccept(SendToAddressDisabledErrorCode,
                "Bitcoin RPC route does not allow sendtoaddress"));

        return route.Client.SendToAddressAsync(request, ct);
    }

    private static BitcoinPayoutRpcRouteKey RouteKey(string poolId, string coin)
    {
        return new BitcoinPayoutRpcRouteKey
        {
            PoolId = poolId,
            Coin = coin
        };
    }

    private static void ValidateRouteKey(BitcoinPayoutRpcRouteKey key, string name)
    {
        if(key == null)
            throw new ArgumentNullException(name);

        RequireText(key.PoolId, nameof(key.PoolId));
        RequireText(key.Coin, nameof(key.Coin));
    }

    private static void RequireText(string value, string name)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required", name);
    }
}
