using System;
using System.Collections.Generic;
using System.Linq;
using HashStormCore.Payments;

namespace HashStormCore.PayoutProcessor.Configuration;

public static class BitcoinRpcPayoutSenderRegistryBuilder
{
    public static IPayoutAttemptSenderRegistry Build(
        PayoutProcessorConfig config,
        PayoutProcessorClusterConfig clusterConfig,
        BitcoinRpcPayoutSenderRegistrationMaterializer materializer)
    {
        return BuildWithReport(config, clusterConfig, materializer).Registry;
    }

    public static BitcoinRpcPayoutSenderRegistryBuildResult BuildWithReport(
        PayoutProcessorConfig config,
        PayoutProcessorClusterConfig clusterConfig,
        BitcoinRpcPayoutSenderRegistrationMaterializer materializer)
    {
        if(config == null)
            throw new ArgumentNullException(nameof(config));

        if(clusterConfig == null)
            throw new ArgumentNullException(nameof(clusterConfig));

        if(materializer == null)
            throw new ArgumentNullException(nameof(materializer));

        var adapters = config.BitcoinRpcAdapters ?? Array.Empty<PayoutProcessorBitcoinRpcAdapterConfig>();
        var enabledAdapters = adapters
            .Select((adapter, index) => new IndexedAdapter(index, adapter))
            .Where(x => x.Adapter?.Enabled == true)
            .ToArray();

        if(!config.Enabled)
            return EmptyResult("bitcoin_rpc_sender_registry_processor_disabled", enabledAdapters.Length);

        if(config.Mode == PayoutProcessorMode.Disabled)
            return EmptyResult("bitcoin_rpc_sender_registry_mode_disabled", enabledAdapters.Length);

        if(config.Mode == PayoutProcessorMode.DryRun)
            return EmptyResult("bitcoin_rpc_sender_registry_dry_run", enabledAdapters.Length);

        if(config.Mode != PayoutProcessorMode.DbMutating)
            return EmptyResult("bitcoin_rpc_sender_registry_mode_not_db_mutating", enabledAdapters.Length);

        if(config.FakeAdaptersOnly)
            return EmptyResult("bitcoin_rpc_sender_registry_fake_adapters_only", enabledAdapters.Length);

        if(enabledAdapters.Length == 0)
            return EmptyResult("bitcoin_rpc_sender_registry_no_enabled_adapters", 0);

        var poolErrors = ValidateClusterPoolGates(enabledAdapters, clusterConfig);
        if(poolErrors.Count > 0)
            throw StartupFailure(poolErrors);

        BitcoinRpcPayoutSenderRegistrationMaterializationResult materializationResult;
        try
        {
            materializationResult = materializer.Materialize(adapters);
        }
        catch(Exception)
        {
            throw StartupFailure(new[]
            {
                Error(-1, string.Empty, string.Empty, "bitcoin_rpc_sender_registry_materializer_failed")
            });
        }

        if(!materializationResult.IsValid)
            throw StartupFailure(materializationResult.Errors
                .Select(x => Error(x.ConfigIndex, x.PoolId, x.Coin, x.Code)));

        if(materializationResult.Registrations.Count == 0)
            throw StartupFailure(new[]
            {
                Error(-1, string.Empty, string.Empty, "bitcoin_rpc_sender_registry_no_registrations")
            });

        try
        {
            var registry = new PayoutAttemptSenderRegistry(materializationResult.Registrations);
            return new BitcoinRpcPayoutSenderRegistryBuildResult
            {
                Registry = registry,
                Report = BitcoinRpcPayoutSenderRegistryBuildReport.Materialized(
                    enabledAdapters.Length,
                    materializationResult.Registrations.Count,
                    materializationResult.Plans.Sum(x => x.Routes.Count),
                    materializationResult.Plans.SelectMany(x => x.Routes)
                        .Select(x => new BitcoinRpcPayoutSenderRegistryRouteReport
                        {
                            ConfigIndex = x.ConfigIndex,
                            PoolId = x.PoolId,
                            Coin = x.Coin,
                            SafeSummary = x.SafeSummary
                        })
                        .ToArray())
            };
        }
        catch(Exception)
        {
            throw StartupFailure(new[]
            {
                Error(-1, string.Empty, string.Empty, "bitcoin_rpc_sender_registry_construction_failed")
            });
        }
    }

    private static BitcoinRpcPayoutSenderRegistryBuildResult EmptyResult(string reasonCode, int enabledAdapterCount)
    {
        return new BitcoinRpcPayoutSenderRegistryBuildResult
        {
            Registry = new PayoutAttemptSenderRegistry(Array.Empty<PayoutAttemptSenderRegistration>()),
            Report = BitcoinRpcPayoutSenderRegistryBuildReport.Empty(reasonCode, enabledAdapterCount)
        };
    }

    private static IReadOnlyCollection<BitcoinRpcPayoutSenderRegistryBuildError> ValidateClusterPoolGates(
        IReadOnlyCollection<IndexedAdapter> enabledAdapters,
        PayoutProcessorClusterConfig clusterConfig)
    {
        var errors = new List<BitcoinRpcPayoutSenderRegistryBuildError>();
        var pools = clusterConfig.Pools ?? Array.Empty<PayoutProcessorClusterPoolConfig>();

        foreach(var indexed in enabledAdapters)
        {
            var adapter = indexed.Adapter;
            var pool = pools.FirstOrDefault(x =>
                string.Equals(x?.Id, adapter.PoolId, StringComparison.Ordinal));

            if(pool == null)
            {
                errors.Add(Error(indexed.Index, adapter.PoolId, adapter.Coin,
                    "bitcoin_rpc_sender_registry_pool_missing"));
                continue;
            }

            if(!pool.Enabled)
                errors.Add(Error(indexed.Index, adapter.PoolId, adapter.Coin,
                    "bitcoin_rpc_sender_registry_pool_disabled"));

            if(pool.PaymentProcessing == null)
            {
                errors.Add(Error(indexed.Index, adapter.PoolId, adapter.Coin,
                    "bitcoin_rpc_sender_registry_pool_payment_processing_missing"));
            }
            else
            {
                if(!pool.PaymentProcessing.Enabled)
                    errors.Add(Error(indexed.Index, adapter.PoolId, adapter.Coin,
                        "bitcoin_rpc_sender_registry_pool_payment_processing_disabled"));

                if(!string.Equals(pool.PaymentProcessing.Engine, "intent", StringComparison.OrdinalIgnoreCase))
                    errors.Add(Error(indexed.Index, adapter.PoolId, adapter.Coin,
                        "bitcoin_rpc_sender_registry_pool_engine_not_intent"));
            }

            if(!string.Equals(pool.Coin, adapter.Coin, StringComparison.OrdinalIgnoreCase))
                errors.Add(Error(indexed.Index, adapter.PoolId, adapter.Coin,
                    "bitcoin_rpc_sender_registry_pool_coin_mismatch"));
        }

        return errors;
    }

    private static InvalidOperationException StartupFailure(
        IEnumerable<BitcoinRpcPayoutSenderRegistryBuildError> errors)
    {
        var summaries = errors.Select(x => x.ToSafeSummary()).ToArray();
        return new InvalidOperationException(
            $"Bitcoin RPC payout sender registry startup gates failed: {string.Join("; ", summaries)}");
    }

    private static BitcoinRpcPayoutSenderRegistryBuildError Error(
        int configIndex,
        string poolId,
        string coin,
        string code)
    {
        return new BitcoinRpcPayoutSenderRegistryBuildError
        {
            ConfigIndex = configIndex,
            PoolId = poolId ?? string.Empty,
            Coin = coin ?? string.Empty,
            Code = code
        };
    }

    private sealed record IndexedAdapter(int Index, PayoutProcessorBitcoinRpcAdapterConfig Adapter);
}

public record BitcoinRpcPayoutSenderRegistryBuildError
{
    public int ConfigIndex { get; init; }
    public string PoolId { get; init; } = string.Empty;
    public string Coin { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;

    public string ToSafeSummary()
    {
        return $"ConfigIndex={ConfigIndex}; PoolId={PoolId}; Coin={Coin}; Code={Code}";
    }

    public override string ToString()
    {
        return ToSafeSummary();
    }
}

public record BitcoinRpcPayoutSenderRegistryBuildResult
{
    public IPayoutAttemptSenderRegistry Registry { get; init; }
    public BitcoinRpcPayoutSenderRegistryBuildReport Report { get; init; } =
        BitcoinRpcPayoutSenderRegistryBuildReport.Empty("bitcoin_rpc_sender_registry_not_built", 0);
}

public record BitcoinRpcPayoutSenderRegistryBuildReport
{
    public string Status { get; init; } = string.Empty;
    public string ReasonCode { get; init; } = string.Empty;
    public int EnabledAdapterCount { get; init; }
    public int RegistrationCount { get; init; }
    public int RouteCount { get; init; }
    public IReadOnlyCollection<BitcoinRpcPayoutSenderRegistryRouteReport> Routes { get; init; } =
        Array.Empty<BitcoinRpcPayoutSenderRegistryRouteReport>();

    public static BitcoinRpcPayoutSenderRegistryBuildReport Empty(string reasonCode, int enabledAdapterCount)
    {
        return new BitcoinRpcPayoutSenderRegistryBuildReport
        {
            Status = "empty",
            ReasonCode = reasonCode,
            EnabledAdapterCount = enabledAdapterCount
        };
    }

    public static BitcoinRpcPayoutSenderRegistryBuildReport Materialized(
        int enabledAdapterCount,
        int registrationCount,
        int routeCount,
        IReadOnlyCollection<BitcoinRpcPayoutSenderRegistryRouteReport> routes)
    {
        return new BitcoinRpcPayoutSenderRegistryBuildReport
        {
            Status = "materialized",
            ReasonCode = "bitcoin_rpc_sender_registry_materialized",
            EnabledAdapterCount = enabledAdapterCount,
            RegistrationCount = registrationCount,
            RouteCount = routeCount,
            Routes = routes ?? Array.Empty<BitcoinRpcPayoutSenderRegistryRouteReport>()
        };
    }

    public string ToSafeSummary()
    {
        var routeSummaries = Routes.Count == 0
            ? string.Empty
            : string.Join(" | ", Routes.Select(x => x.ToSafeSummary()));

        return
            $"Status={Status}; ReasonCode={ReasonCode}; EnabledAdapterCount={EnabledAdapterCount}; RegistrationCount={RegistrationCount}; RouteCount={RouteCount}; Routes=[{routeSummaries}]";
    }

    public override string ToString()
    {
        return ToSafeSummary();
    }
}

public record BitcoinRpcPayoutSenderRegistryRouteReport
{
    public int ConfigIndex { get; init; }
    public string PoolId { get; init; } = string.Empty;
    public string Coin { get; init; } = string.Empty;
    public string SafeSummary { get; init; } = string.Empty;

    public string ToSafeSummary()
    {
        return
            $"ConfigIndex={ConfigIndex}; PoolId={PoolId}; Coin={Coin}; SafeSummarySet={!string.IsNullOrWhiteSpace(SafeSummary)}; Summary={SafeSummary}";
    }

    public override string ToString()
    {
        return ToSafeSummary();
    }
}
