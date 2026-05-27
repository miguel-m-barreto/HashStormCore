using HashStormCore.Payments;
using HashStormCore.Payouts.Bitcoin;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.PayoutProcessor.Configuration;

public record BitcoinRpcPayoutSenderRegistrationPlan
{
    public PayoutAttemptSenderKey Key { get; init; }
    public string CoinFamily { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
    public string SendShape { get; init; } = string.Empty;
    public string SendMethod { get; init; } = string.Empty;
    public IReadOnlyCollection<BitcoinRpcPayoutSenderRoutePlan> Routes { get; init; } =
        Array.Empty<BitcoinRpcPayoutSenderRoutePlan>();
}

public record BitcoinRpcPayoutSenderRoutePlan
{
    public int ConfigIndex { get; init; }
    public string PoolId { get; init; } = string.Empty;
    public string Coin { get; init; } = string.Empty;
    public bool AllowSendMany { get; init; }
    public bool AllowSendToAddress { get; init; }
    public string SafeSummary { get; init; } = string.Empty;
}

public record BitcoinRpcPayoutSenderRegistrationPlanResult(
    IReadOnlyCollection<BitcoinRpcPayoutSenderRegistrationPlan> Plans,
    IReadOnlyCollection<BitcoinRpcPayoutSenderRegistrationPlanError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public record BitcoinRpcPayoutSenderRegistrationPlanError
{
    public int ConfigIndex { get; init; }
    public string PoolId { get; init; } = string.Empty;
    public string Coin { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public static class BitcoinRpcPayoutSenderRegistrationPlanner
{
    public static BitcoinRpcPayoutSenderRegistrationPlanResult CreatePlans(
        IEnumerable<PayoutProcessorBitcoinRpcAdapterConfig> configs,
        IPayoutProfileResolver profileResolver)
    {
        if(profileResolver == null)
            throw new ArgumentNullException(nameof(profileResolver));

        var configArray = configs?.ToArray() ?? Array.Empty<PayoutProcessorBitcoinRpcAdapterConfig>();
        var validationResult = PayoutProcessorBitcoinRpcAdapterValidator.Validate(configArray);
        var errors = validationResult.Errors
            .Select(x => Error(x.Index, x.PoolId, x.Coin, x.Code, x.Message))
            .ToList();

        if(errors.Count > 0)
            return new BitcoinRpcPayoutSenderRegistrationPlanResult(
                Array.Empty<BitcoinRpcPayoutSenderRegistrationPlan>(), errors);

        var planBuilders = new Dictionary<PayoutAttemptSenderKey, BitcoinRpcPayoutSenderRegistrationPlanBuilder>();

        for(var i = 0; i < configArray.Length; i++)
        {
            var config = configArray[i];
            if(config == null || !config.Enabled)
                continue;

            var resolution = profileResolver.Resolve(config.Coin);
            if(!resolution.HasProfile)
            {
                errors.Add(Error(i, config.PoolId, config.Coin, "bitcoin_rpc_adapter_profile_unresolved",
                    "Enabled Bitcoin RPC adapter config coin does not resolve to a payout profile"));
                continue;
            }

            var profile = resolution.Profile;
            if(!profile.ReservationReady)
            {
                errors.Add(Error(i, config.PoolId, config.Coin, "bitcoin_rpc_adapter_profile_not_ready",
                    "Enabled Bitcoin RPC adapter config profile is not reservation ready"));
                continue;
            }

            var profileError = ValidateProfile(config, profile);
            if(profileError != null)
            {
                errors.Add(profileError with { ConfigIndex = i, PoolId = config.PoolId, Coin = config.Coin });
                continue;
            }

            var key = new PayoutAttemptSenderKey
            {
                CoinFamily = profile.CoinFamily,
                AdapterId = profile.AdapterId,
                SendShape = profile.SendShape,
                SendMethod = profile.SendMethod
            };

            if(!planBuilders.TryGetValue(key, out var builder))
            {
                builder = new BitcoinRpcPayoutSenderRegistrationPlanBuilder(key, profile);
                planBuilders.Add(key, builder);
            }

            builder.Routes.Add(new BitcoinRpcPayoutSenderRoutePlan
            {
                ConfigIndex = i,
                PoolId = config.PoolId,
                Coin = config.Coin,
                AllowSendMany = config.AllowSendMany,
                AllowSendToAddress = config.AllowSendToAddress,
                SafeSummary = config.ToSafeSummary()
            });
        }

        if(errors.Count > 0)
            return new BitcoinRpcPayoutSenderRegistrationPlanResult(
                Array.Empty<BitcoinRpcPayoutSenderRegistrationPlan>(), errors);

        var plans = planBuilders.Values.Select(x => x.ToPlan()).ToArray();
        return new BitcoinRpcPayoutSenderRegistrationPlanResult(plans, Array.Empty<BitcoinRpcPayoutSenderRegistrationPlanError>());
    }

    private static BitcoinRpcPayoutSenderRegistrationPlanError ValidateProfile(
        PayoutProcessorBitcoinRpcAdapterConfig config,
        PayoutProfile profile)
    {
        if(!string.Equals(profile.AdapterId, PayoutProfileConstants.AdapterIds.BitcoinRpc, StringComparison.Ordinal))
            return Error(0, config.PoolId, config.Coin, "bitcoin_rpc_adapter_profile_not_bitcoin_rpc",
                "Enabled Bitcoin RPC adapter config profile does not use the bitcoin-rpc adapter");

        if(!profile.SupportsTransparentTxId)
            return Error(0, config.PoolId, config.Coin, "bitcoin_rpc_adapter_profile_without_transparent_txid",
                "Enabled Bitcoin RPC adapter config profile does not support transparent transaction id evidence");

        if(!string.Equals(profile.SettlementEvidenceKind, PayoutProfileConstants.SettlementEvidenceKinds.TxId,
               StringComparison.Ordinal))
            return Error(0, config.PoolId, config.Coin, "bitcoin_rpc_adapter_profile_wrong_settlement_evidence",
                "Enabled Bitcoin RPC adapter config profile does not settle with transaction id evidence");

        if(!IsSupportedSendShape(profile.SendShape))
            return Error(0, config.PoolId, config.Coin, "bitcoin_rpc_adapter_profile_unsupported_send_shape",
                "Enabled Bitcoin RPC adapter config profile send shape is not supported by the Bitcoin RPC sender");

        if(!IsSupportedSendMethod(profile.SendMethod))
            return Error(0, config.PoolId, config.Coin, "bitcoin_rpc_adapter_profile_unsupported_send_method",
                "Enabled Bitcoin RPC adapter config profile send method is not supported by the Bitcoin RPC sender");

        if(!IsSupportedShapeMethodPair(profile.SendShape, profile.SendMethod))
            return Error(0, config.PoolId, config.Coin, "bitcoin_rpc_adapter_profile_unsupported_shape_method",
                "Enabled Bitcoin RPC adapter config profile send shape and method are not supported together by the Bitcoin RPC sender");

        if(string.Equals(profile.SendMethod, PayoutProfileConstants.SendMethods.SendMany, StringComparison.Ordinal) &&
           !config.AllowSendMany)
            return Error(0, config.PoolId, config.Coin, "bitcoin_rpc_adapter_sendmany_disabled",
                "Enabled Bitcoin RPC adapter config disables sendmany required by the resolved profile");

        if(string.Equals(profile.SendMethod, PayoutProfileConstants.SendMethods.SendToAddress, StringComparison.Ordinal) &&
           !config.AllowSendToAddress)
            return Error(0, config.PoolId, config.Coin, "bitcoin_rpc_adapter_sendtoaddress_disabled",
                "Enabled Bitcoin RPC adapter config disables sendtoaddress required by the resolved profile");

        return null;
    }

    private static bool IsSupportedSendShape(string sendShape)
    {
        return string.Equals(sendShape, PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                   StringComparison.Ordinal) ||
               string.Equals(sendShape, PayoutProfileConstants.SendShapes.PerAddress, StringComparison.Ordinal);
    }

    private static bool IsSupportedSendMethod(string sendMethod)
    {
        return string.Equals(sendMethod, PayoutProfileConstants.SendMethods.SendMany, StringComparison.Ordinal) ||
               string.Equals(sendMethod, PayoutProfileConstants.SendMethods.SendToAddress, StringComparison.Ordinal);
    }

    private static bool IsSupportedShapeMethodPair(string sendShape, string sendMethod)
    {
        return (string.Equals(sendShape, PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                    StringComparison.Ordinal) &&
                string.Equals(sendMethod, PayoutProfileConstants.SendMethods.SendMany, StringComparison.Ordinal)) ||
               (string.Equals(sendShape, PayoutProfileConstants.SendShapes.PerAddress, StringComparison.Ordinal) &&
                string.Equals(sendMethod, PayoutProfileConstants.SendMethods.SendToAddress, StringComparison.Ordinal));
    }

    private static BitcoinRpcPayoutSenderRegistrationPlanError Error(
        int configIndex,
        string poolId,
        string coin,
        string code,
        string message)
    {
        return new BitcoinRpcPayoutSenderRegistrationPlanError
        {
            ConfigIndex = configIndex,
            PoolId = poolId ?? string.Empty,
            Coin = coin ?? string.Empty,
            Code = code,
            Message = message
        };
    }
}

public static class BitcoinRpcPayoutSenderRegistrationFactory
{
    public static IReadOnlyCollection<PayoutAttemptSenderRegistration> CreateRegistrations(
        IEnumerable<BitcoinRpcPayoutSenderRegistrationPlan> plans,
        IReadOnlyDictionary<BitcoinPayoutRpcRouteKey, IBitcoinPayoutRpcClient> routeClients)
    {
        if(plans == null)
            throw new ArgumentNullException(nameof(plans));

        if(routeClients == null)
            throw new ArgumentNullException(nameof(routeClients));

        return plans.Select(x => new PayoutAttemptSenderRegistration
            {
                Key = x.Key,
                Sender = new BitcoinRpcPayoutSender(new BitcoinPayoutRpcRoutingClient(CreateRouteRegistrations(x,
                    routeClients)))
            })
            .ToArray();
    }

    private static IReadOnlyCollection<BitcoinPayoutRpcRouteRegistration> CreateRouteRegistrations(
        BitcoinRpcPayoutSenderRegistrationPlan plan,
        IReadOnlyDictionary<BitcoinPayoutRpcRouteKey, IBitcoinPayoutRpcClient> routeClients)
    {
        if(plan == null)
            throw new ArgumentNullException(nameof(plan));

        return plan.Routes.Select(route =>
            {
                var routeKey = new BitcoinPayoutRpcRouteKey
                {
                    PoolId = route.PoolId,
                    Coin = route.Coin
                };

                if(!routeClients.TryGetValue(routeKey, out var client))
                    throw new InvalidOperationException("Bitcoin RPC route client is missing for a planned route");

                return new BitcoinPayoutRpcRouteRegistration
                {
                    RouteKey = routeKey,
                    Client = client,
                    AllowSendMany = route.AllowSendMany,
                    AllowSendToAddress = route.AllowSendToAddress,
                    SafeSummary = route.SafeSummary
                };
            })
            .ToArray();
    }
}

internal sealed class BitcoinRpcPayoutSenderRegistrationPlanBuilder
{
    public BitcoinRpcPayoutSenderRegistrationPlanBuilder(PayoutAttemptSenderKey key, PayoutProfile profile)
    {
        Key = key;
        CoinFamily = profile.CoinFamily;
        AdapterId = profile.AdapterId;
        SendShape = profile.SendShape;
        SendMethod = profile.SendMethod;
    }

    public PayoutAttemptSenderKey Key { get; }
    public string CoinFamily { get; }
    public string AdapterId { get; }
    public string SendShape { get; }
    public string SendMethod { get; }
    public List<BitcoinRpcPayoutSenderRoutePlan> Routes { get; } = new();

    public BitcoinRpcPayoutSenderRegistrationPlan ToPlan()
    {
        return new BitcoinRpcPayoutSenderRegistrationPlan
        {
            Key = Key,
            CoinFamily = CoinFamily,
            AdapterId = AdapterId,
            SendShape = SendShape,
            SendMethod = SendMethod,
            Routes = Routes.ToArray()
        };
    }
}
