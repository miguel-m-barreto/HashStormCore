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

        if(!config.Enabled ||
           config.Mode != PayoutProcessorMode.DbMutating ||
           config.FakeAdaptersOnly ||
           enabledAdapters.Length == 0)
            return EmptyRegistry();

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
            return new PayoutAttemptSenderRegistry(materializationResult.Registrations);
        }
        catch(Exception)
        {
            throw StartupFailure(new[]
            {
                Error(-1, string.Empty, string.Empty, "bitcoin_rpc_sender_registry_construction_failed")
            });
        }
    }

    private static PayoutAttemptSenderRegistry EmptyRegistry()
    {
        return new PayoutAttemptSenderRegistry(Array.Empty<PayoutAttemptSenderRegistration>());
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
