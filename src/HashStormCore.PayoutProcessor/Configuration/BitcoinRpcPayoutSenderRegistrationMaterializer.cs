using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using HashStormCore.Payments;
using HashStormCore.Payouts.Bitcoin;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.PayoutProcessor.Configuration;

public class BitcoinRpcPayoutSenderRegistrationMaterializer
{
    public BitcoinRpcPayoutSenderRegistrationMaterializer(
        IPayoutProfileResolver profileResolver,
        IBitcoinJsonRpcHttpClientProvider httpClientProvider)
    {
        this.profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        this.httpClientProvider = httpClientProvider ?? throw new ArgumentNullException(nameof(httpClientProvider));
    }

    private readonly IPayoutProfileResolver profileResolver;
    private readonly IBitcoinJsonRpcHttpClientProvider httpClientProvider;

    public BitcoinRpcPayoutSenderRegistrationMaterializationResult Materialize(
        IEnumerable<PayoutProcessorBitcoinRpcAdapterConfig> configs)
    {
        var configArray = configs?.ToArray() ?? Array.Empty<PayoutProcessorBitcoinRpcAdapterConfig>();
        var planResult = BitcoinRpcPayoutSenderRegistrationPlanner.CreatePlans(configArray, profileResolver);
        var planErrors = planResult.Errors
            .Select(BitcoinRpcPayoutSenderRegistrationMaterializationError.FromPlanError)
            .ToList();

        if(planErrors.Count > 0)
            return BitcoinRpcPayoutSenderRegistrationMaterializationResult.Failed(planErrors, planResult.Plans);

        var enabledConfigLookup = BuildEnabledConfigLookup(configArray);
        if(enabledConfigLookup.Errors.Count > 0)
            return BitcoinRpcPayoutSenderRegistrationMaterializationResult.Failed(enabledConfigLookup.Errors,
                planResult.Plans);

        var routeClients = new Dictionary<BitcoinPayoutRpcRouteKey, IBitcoinPayoutRpcClient>();
        var errors = new List<BitcoinRpcPayoutSenderRegistrationMaterializationError>();

        foreach(var plan in planResult.Plans)
        {
            foreach(var route in plan.Routes)
            {
                var lookupKey = RouteLookupKey(route.PoolId, route.Coin);
                if(!enabledConfigLookup.Configs.TryGetValue(lookupKey, out var config))
                {
                    errors.Add(Error(route.ConfigIndex, route.PoolId, route.Coin,
                        "bitcoin_rpc_materializer_missing_config",
                        "Bitcoin RPC planned route does not have a matching enabled config entry"));
                    continue;
                }

                if(!string.Equals(route.PoolId, config.PoolId, StringComparison.Ordinal) ||
                   !string.Equals(route.Coin, config.Coin, StringComparison.Ordinal))
                {
                    errors.Add(Error(route.ConfigIndex, route.PoolId, route.Coin,
                        "bitcoin_rpc_materializer_route_config_mismatch",
                        "Bitcoin RPC planned route does not match its enabled config entry"));
                    continue;
                }

                var routeKey = new BitcoinPayoutRpcRouteKey
                {
                    PoolId = route.PoolId,
                    Coin = route.Coin
                };

                if(routeClients.ContainsKey(routeKey))
                {
                    errors.Add(Error(route.ConfigIndex, route.PoolId, route.Coin,
                        "bitcoin_rpc_materializer_duplicate_planned_route",
                        "Bitcoin RPC planned route appears more than once"));
                    continue;
                }

                var routeClient = CreateRouteClient(route, config, errors);
                if(routeClient != null)
                    routeClients.Add(routeKey, routeClient);
            }
        }

        if(errors.Count > 0)
            return BitcoinRpcPayoutSenderRegistrationMaterializationResult.Failed(errors, planResult.Plans);

        try
        {
            var registrations = BitcoinRpcPayoutSenderRegistrationFactory.CreateRegistrations(planResult.Plans,
                routeClients);

            return new BitcoinRpcPayoutSenderRegistrationMaterializationResult
            {
                Registrations = registrations.ToArray(),
                Plans = planResult.Plans.ToArray(),
                Errors = Array.Empty<BitcoinRpcPayoutSenderRegistrationMaterializationError>()
            };
        }
        catch(Exception)
        {
            return BitcoinRpcPayoutSenderRegistrationMaterializationResult.Failed(new[]
            {
                Error(-1, string.Empty, string.Empty, "bitcoin_rpc_materializer_factory_failed",
                    "Bitcoin RPC sender registration factory failed")
            }, planResult.Plans);
        }
    }

    private IBitcoinPayoutRpcClient CreateRouteClient(
        BitcoinRpcPayoutSenderRoutePlan route,
        PayoutProcessorBitcoinRpcAdapterConfig config,
        ICollection<BitcoinRpcPayoutSenderRegistrationMaterializationError> errors)
    {
        HttpClient httpClient;
        try
        {
            httpClient = httpClientProvider.GetHttpClient(new BitcoinJsonRpcHttpClientRoute
            {
                ConfigIndex = route.ConfigIndex,
                PoolId = route.PoolId,
                Coin = route.Coin,
                SafeSummary = route.SafeSummary
            });
        }
        catch(Exception)
        {
            errors.Add(Error(route.ConfigIndex, route.PoolId, route.Coin,
                "bitcoin_rpc_materializer_http_client_provider_failed",
                "Bitcoin RPC HTTP client provider failed for a planned route"));
            return null;
        }

        if(httpClient == null)
        {
            errors.Add(Error(route.ConfigIndex, route.PoolId, route.Coin,
                "bitcoin_rpc_materializer_missing_http_client",
                "Bitcoin RPC HTTP client provider returned no client for a planned route"));
            return null;
        }

        try
        {
            var options = new BitcoinJsonRpcRouteOptions
            {
                Endpoint = config.Endpoint,
                Username = config.Username,
                Password = config.Password,
                WalletName = config.WalletName,
                WalletPassphrase = config.WalletPassphrase,
                WalletUnlockSeconds = config.WalletUnlockSeconds,
                LockWalletAfterSend = config.LockWalletAfterSend,
                RequestTimeoutSeconds = config.RequestTimeoutSeconds
            };

            return new BitcoinJsonRpcPayoutClient(new BitcoinJsonRpcHttpTransport(httpClient), options);
        }
        catch(Exception)
        {
            errors.Add(Error(route.ConfigIndex, route.PoolId, route.Coin,
                "bitcoin_rpc_materializer_route_options_invalid",
                "Bitcoin RPC route options are invalid"));
            return null;
        }
    }

    private static EnabledConfigLookup BuildEnabledConfigLookup(
        IReadOnlyList<PayoutProcessorBitcoinRpcAdapterConfig> configs)
    {
        var lookup = new Dictionary<string, PayoutProcessorBitcoinRpcAdapterConfig>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<BitcoinRpcPayoutSenderRegistrationMaterializationError>();

        for(var i = 0; i < configs.Count; i++)
        {
            var config = configs[i];
            if(config == null || !config.Enabled)
                continue;

            var key = RouteLookupKey(config.PoolId, config.Coin);
            if(!lookup.TryAdd(key, config))
            {
                errors.Add(Error(i, config.PoolId, config.Coin,
                    "bitcoin_rpc_materializer_duplicate_config_lookup",
                    "Bitcoin RPC enabled config lookup contains a duplicate PoolId and Coin"));
            }
        }

        return new EnabledConfigLookup(lookup, errors);
    }

    private static string RouteLookupKey(string poolId, string coin)
    {
        return $"{poolId ?? string.Empty}\n{coin ?? string.Empty}";
    }

    private static BitcoinRpcPayoutSenderRegistrationMaterializationError Error(
        int configIndex,
        string poolId,
        string coin,
        string code,
        string message)
    {
        return new BitcoinRpcPayoutSenderRegistrationMaterializationError
        {
            ConfigIndex = configIndex,
            PoolId = poolId ?? string.Empty,
            Coin = coin ?? string.Empty,
            Code = code,
            Message = message
        };
    }

    private sealed record EnabledConfigLookup(
        IReadOnlyDictionary<string, PayoutProcessorBitcoinRpcAdapterConfig> Configs,
        IReadOnlyCollection<BitcoinRpcPayoutSenderRegistrationMaterializationError> Errors);
}

public record BitcoinRpcPayoutSenderRegistrationMaterializationResult
{
    public IReadOnlyCollection<PayoutAttemptSenderRegistration> Registrations { get; init; } =
        Array.Empty<PayoutAttemptSenderRegistration>();
    public IReadOnlyCollection<BitcoinRpcPayoutSenderRegistrationPlan> Plans { get; init; } =
        Array.Empty<BitcoinRpcPayoutSenderRegistrationPlan>();
    public IReadOnlyCollection<BitcoinRpcPayoutSenderRegistrationMaterializationError> Errors { get; init; } =
        Array.Empty<BitcoinRpcPayoutSenderRegistrationMaterializationError>();

    public bool IsValid => Errors.Count == 0;

    public string ToSafeSummary()
    {
        return
            $"Registrations={Registrations.Count}; Plans={Plans.Count}; Errors={Errors.Count}";
    }

    public override string ToString()
    {
        return ToSafeSummary();
    }

    internal static BitcoinRpcPayoutSenderRegistrationMaterializationResult Failed(
        IEnumerable<BitcoinRpcPayoutSenderRegistrationMaterializationError> errors,
        IReadOnlyCollection<BitcoinRpcPayoutSenderRegistrationPlan> plans)
    {
        return new BitcoinRpcPayoutSenderRegistrationMaterializationResult
        {
            Registrations = Array.Empty<PayoutAttemptSenderRegistration>(),
            Plans = plans?.ToArray() ?? Array.Empty<BitcoinRpcPayoutSenderRegistrationPlan>(),
            Errors = errors?.ToArray() ?? Array.Empty<BitcoinRpcPayoutSenderRegistrationMaterializationError>()
        };
    }
}

public record BitcoinRpcPayoutSenderRegistrationMaterializationError
{
    public int ConfigIndex { get; init; }
    public string PoolId { get; init; } = string.Empty;
    public string Coin { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public string ToSafeSummary()
    {
        return $"ConfigIndex={ConfigIndex}; PoolId={PoolId}; Coin={Coin}; Code={Code}";
    }

    public override string ToString()
    {
        return ToSafeSummary();
    }

    internal static BitcoinRpcPayoutSenderRegistrationMaterializationError FromPlanError(
        BitcoinRpcPayoutSenderRegistrationPlanError error)
    {
        return new BitcoinRpcPayoutSenderRegistrationMaterializationError
        {
            ConfigIndex = error.ConfigIndex,
            PoolId = error.PoolId,
            Coin = error.Coin,
            Code = error.Code,
            Message = error.Message
        };
    }
}
