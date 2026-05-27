namespace HashStormCore.PayoutProcessor.Configuration;

public class PayoutProcessorBitcoinRpcAdapterConfig
{
    public bool Enabled { get; set; } = false;
    public string PoolId { get; set; } = string.Empty;
    public string Coin { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string WalletName { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public bool AllowSendMany { get; set; } = true;
    public bool AllowSendToAddress { get; set; } = true;

    public string ToSafeSummary()
    {
        return
            $"Enabled={Enabled}; PoolId={PoolId}; Coin={Coin}; EndpointSet={!string.IsNullOrWhiteSpace(Endpoint)}; WalletName={WalletName}; UsernameSet={!string.IsNullOrWhiteSpace(Username)}; RequestTimeoutSeconds={RequestTimeoutSeconds}; AllowSendMany={AllowSendMany}; AllowSendToAddress={AllowSendToAddress}";
    }
}

public static class PayoutProcessorBitcoinRpcAdapterValidator
{
    public static PayoutProcessorBitcoinRpcAdapterValidationResult Validate(
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
            RequireNonEmpty(errors, index, adapter, adapter.Coin, "bitcoin_rpc_adapter_missing_coin",
                "Enabled Bitcoin RPC adapter config requires Coin");
            RequireNonEmpty(errors, index, adapter, adapter.Endpoint, "bitcoin_rpc_adapter_missing_endpoint",
                "Enabled Bitcoin RPC adapter config requires Endpoint");
            RequireNonEmpty(errors, index, adapter, adapter.Username, "bitcoin_rpc_adapter_missing_username",
                "Enabled Bitcoin RPC adapter config requires Username");
            RequireNonEmpty(errors, index, adapter, adapter.Password, "bitcoin_rpc_adapter_missing_password",
                "Enabled Bitcoin RPC adapter config requires Password");

            if(EndpointContainsCredentials(adapter.Endpoint))
                errors.Add(Error(index, adapter.PoolId, adapter.Coin,
                    "bitcoin_rpc_adapter_endpoint_contains_credentials",
                    "Enabled Bitcoin RPC adapter config endpoint must not include URI userinfo credentials"));

            if(adapter.RequestTimeoutSeconds <= 0)
                errors.Add(Error(index, adapter.PoolId, adapter.Coin, "bitcoin_rpc_adapter_invalid_timeout",
                    "Enabled Bitcoin RPC adapter config requires RequestTimeoutSeconds greater than zero"));

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

    private static bool EndpointContainsCredentials(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
               !string.IsNullOrEmpty(uri.UserInfo);
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
