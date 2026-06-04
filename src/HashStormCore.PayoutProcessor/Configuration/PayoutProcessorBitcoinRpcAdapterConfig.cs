using System;
using System.Collections.Generic;

namespace HashStormCore.PayoutProcessor.Configuration;

public class PayoutProcessorBitcoinRpcAdapterConfig
{
    public const int MaxWalletUnlockSeconds = 3600;

    public bool Enabled { get; set; } = false;
    public string PoolId { get; set; } = string.Empty;
    public string Coin { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string WalletName { get; set; } = string.Empty;
    public string WalletPassphrase { get; set; } = string.Empty;
    public int WalletUnlockSeconds { get; set; }
    public bool LockWalletAfterSend { get; set; } = true;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public bool AllowSendMany { get; set; } = true;
    public bool AllowSendToAddress { get; set; } = true;

    public string ToSafeSummary()
    {
        return
            $"Enabled={Enabled}; PoolId={PoolId}; Coin={Coin}; EndpointSet={!string.IsNullOrWhiteSpace(Endpoint)}; WalletNameSet={!string.IsNullOrWhiteSpace(WalletName)}; UsernameSet={!string.IsNullOrWhiteSpace(Username)}; WalletPassphraseSet={!string.IsNullOrEmpty(WalletPassphrase)}; WalletUnlockSeconds={WalletUnlockSeconds}; LockWalletAfterSend={LockWalletAfterSend}; RequestTimeoutSeconds={RequestTimeoutSeconds}; AllowSendMany={AllowSendMany}; AllowSendToAddress={AllowSendToAddress}";
    }
}

public static class PayoutProcessorBitcoinRpcAdapterValidator
{
    private static readonly HashSet<string> SupportedEndpointSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https"
    };

    public static PayoutProcessorBitcoinRpcAdapterValidationResult Validate(
        IEnumerable<PayoutProcessorBitcoinRpcAdapterConfig> adapters)
    {
        return ValidateAdapters(adapters);
    }

    public static PayoutProcessorBitcoinRpcAdapterValidationResult ValidateAdapters(
        IEnumerable<PayoutProcessorBitcoinRpcAdapterConfig> adapters)
    {
        var errors = new List<PayoutProcessorBitcoinRpcAdapterValidationError>();
        var enabledKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if(adapters == null)
            return new PayoutProcessorBitcoinRpcAdapterValidationResult(Array.Empty<PayoutProcessorBitcoinRpcAdapterValidationError>());

        var index = 0;
        foreach(var adapter in adapters)
        {
            if(adapter == null)
            {
                errors.Add(Error(index, null, null, "bitcoin_rpc_adapter_null_entry",
                    "Bitcoin RPC adapter config entry is null"));
                index++;
                continue;
            }

            if(!adapter.Enabled)
            {
                index++;
                continue;
            }

            RequireNonEmpty(errors, index, adapter, adapter.PoolId, "bitcoin_rpc_adapter_missing_pool_id",
                "Enabled Bitcoin RPC adapter config requires PoolId");
            RequireNoOuterWhitespace(errors, index, adapter, adapter.PoolId,
                "bitcoin_rpc_adapter_poolid_has_outer_whitespace",
                "Enabled Bitcoin RPC adapter config PoolId must not include leading or trailing whitespace");
            RequireNonEmpty(errors, index, adapter, adapter.Coin, "bitcoin_rpc_adapter_missing_coin",
                "Enabled Bitcoin RPC adapter config requires Coin");
            RequireNoOuterWhitespace(errors, index, adapter, adapter.Coin,
                "bitcoin_rpc_adapter_coin_has_outer_whitespace",
                "Enabled Bitcoin RPC adapter config Coin must not include leading or trailing whitespace");
            RequireNonEmpty(errors, index, adapter, adapter.Endpoint, "bitcoin_rpc_adapter_missing_endpoint",
                "Enabled Bitcoin RPC adapter config requires Endpoint");
            RequireNonEmpty(errors, index, adapter, adapter.Username, "bitcoin_rpc_adapter_missing_username",
                "Enabled Bitcoin RPC adapter config requires Username");
            RequireNoOuterWhitespace(errors, index, adapter, adapter.Username,
                "bitcoin_rpc_adapter_username_has_outer_whitespace",
                "Enabled Bitcoin RPC adapter config Username must not include leading or trailing whitespace");
            RequireNonEmptyPassword(errors, index, adapter, adapter.Password, "bitcoin_rpc_adapter_missing_password",
                "Enabled Bitcoin RPC adapter config requires Password");

            ValidateEndpoint(errors, index, adapter);

            if(adapter.RequestTimeoutSeconds <= 0)
                errors.Add(Error(index, adapter.PoolId, adapter.Coin, "bitcoin_rpc_adapter_invalid_timeout",
                    "Enabled Bitcoin RPC adapter config requires RequestTimeoutSeconds greater than zero"));

            ValidateWalletUnlock(errors, index, adapter);

            if(!adapter.AllowSendMany && !adapter.AllowSendToAddress)
                errors.Add(Error(index, adapter.PoolId, adapter.Coin, "bitcoin_rpc_adapter_no_allowed_methods",
                    "Enabled Bitcoin RPC adapter config requires at least one allowed send method"));

            if(!string.IsNullOrWhiteSpace(adapter.PoolId) && !string.IsNullOrWhiteSpace(adapter.Coin))
            {
                var key = $"{adapter.PoolId.Trim()}\n{adapter.Coin.Trim()}";
                if(enabledKeys.TryGetValue(key, out var firstIndex))
                    errors.Add(Error(index, adapter.PoolId, adapter.Coin, "bitcoin_rpc_adapter_duplicate_pool_coin",
                        $"Enabled Bitcoin RPC adapter config duplicates PoolId and Coin from entry {firstIndex}"));
                else
                    enabledKeys.Add(key, index);
            }

            index++;
        }

        return new PayoutProcessorBitcoinRpcAdapterValidationResult(errors);
    }

    private static void RequireNonEmpty(
        ICollection<PayoutProcessorBitcoinRpcAdapterValidationError> errors,
        int index,
        PayoutProcessorBitcoinRpcAdapterConfig adapter,
        string value,
        string code,
        string message)
    {
        if(string.IsNullOrWhiteSpace(value))
            errors.Add(Error(index, adapter.PoolId, adapter.Coin, code, message));
    }

    private static void RequireNonEmptyPassword(
        ICollection<PayoutProcessorBitcoinRpcAdapterValidationError> errors,
        int index,
        PayoutProcessorBitcoinRpcAdapterConfig adapter,
        string value,
        string code,
        string message)
    {
        if(string.IsNullOrWhiteSpace(value))
            errors.Add(Error(index, adapter.PoolId, adapter.Coin, code, message));
    }

    private static void RequireNoOuterWhitespace(
        ICollection<PayoutProcessorBitcoinRpcAdapterValidationError> errors,
        int index,
        PayoutProcessorBitcoinRpcAdapterConfig adapter,
        string value,
        string code,
        string message)
    {
        if(string.IsNullOrEmpty(value) || string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return;

        errors.Add(Error(index, adapter.PoolId, adapter.Coin, code, message));
    }

    private static void ValidateEndpoint(
        ICollection<PayoutProcessorBitcoinRpcAdapterValidationError> errors,
        int index,
        PayoutProcessorBitcoinRpcAdapterConfig adapter)
    {
        if(string.IsNullOrWhiteSpace(adapter.Endpoint))
            return;

        if(!Uri.TryCreate(adapter.Endpoint, UriKind.Absolute, out var uri))
        {
            errors.Add(Error(index, adapter.PoolId, adapter.Coin,
                "bitcoin_rpc_adapter_endpoint_invalid",
                "Enabled Bitcoin RPC adapter config endpoint must be an absolute URI"));
            return;
        }

        if(!SupportedEndpointSchemes.Contains(uri.Scheme))
            errors.Add(Error(index, adapter.PoolId, adapter.Coin,
                "bitcoin_rpc_adapter_endpoint_scheme_unsupported",
                "Enabled Bitcoin RPC adapter config endpoint scheme must be http or https"));

        if(!string.IsNullOrEmpty(uri.UserInfo))
            errors.Add(Error(index, adapter.PoolId, adapter.Coin,
                "bitcoin_rpc_adapter_endpoint_contains_credentials",
                "Enabled Bitcoin RPC adapter config endpoint must not include URI userinfo credentials"));
    }

    private static void ValidateWalletUnlock(
        ICollection<PayoutProcessorBitcoinRpcAdapterValidationError> errors,
        int index,
        PayoutProcessorBitcoinRpcAdapterConfig adapter)
    {
        var hasPassphrase = !string.IsNullOrEmpty(adapter.WalletPassphrase);
        if(hasPassphrase && adapter.WalletUnlockSeconds <= 0)
        {
            errors.Add(Error(index, adapter.PoolId, adapter.Coin,
                "bitcoin_rpc_adapter_wallet_passphrase_requires_unlock_seconds",
                "Enabled Bitcoin RPC adapter config WalletPassphrase requires WalletUnlockSeconds greater than zero"));
        }

        if(!hasPassphrase && adapter.WalletUnlockSeconds > 0)
        {
            errors.Add(Error(index, adapter.PoolId, adapter.Coin,
                "bitcoin_rpc_adapter_wallet_unlock_requires_passphrase",
                "Enabled Bitcoin RPC adapter config WalletUnlockSeconds requires WalletPassphrase"));
        }

        if(adapter.WalletUnlockSeconds > PayoutProcessorBitcoinRpcAdapterConfig.MaxWalletUnlockSeconds)
        {
            errors.Add(Error(index, adapter.PoolId, adapter.Coin,
                "bitcoin_rpc_adapter_wallet_unlock_seconds_too_large",
                "Enabled Bitcoin RPC adapter config WalletUnlockSeconds exceeds the maximum allowed duration"));
        }
    }

    private static PayoutProcessorBitcoinRpcAdapterValidationError Error(
        int index,
        string poolId,
        string coin,
        string code,
        string message)
    {
        return new PayoutProcessorBitcoinRpcAdapterValidationError
        {
            Index = index,
            PoolId = poolId ?? string.Empty,
            Coin = coin ?? string.Empty,
            Code = code,
            Message = message
        };
    }
}

public record PayoutProcessorBitcoinRpcAdapterValidationResult(
    IReadOnlyCollection<PayoutProcessorBitcoinRpcAdapterValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public record PayoutProcessorBitcoinRpcAdapterValidationError
{
    public int Index { get; init; }
    public string PoolId { get; init; } = string.Empty;
    public string Coin { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
