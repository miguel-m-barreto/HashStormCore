using System.Collections.ObjectModel;
using System.Text.Json;

namespace HashStormCore.Payouts.Bitcoin;

public interface IBitcoinJsonRpcTransport
{
    Task<BitcoinJsonRpcTransportResponse> SendAsync(BitcoinJsonRpcTransportRequest request, CancellationToken ct);
}

public class BitcoinJsonRpcRouteOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string WalletName { get; init; } = string.Empty;
    public string WalletPassphrase { get; init; } = string.Empty;
    public int WalletUnlockSeconds { get; init; }
    public bool LockWalletAfterSend { get; init; } = true;
    public int RequestTimeoutSeconds { get; init; } = 30;

    public string ToSafeSummary()
    {
        return
            $"EndpointSet={!string.IsNullOrWhiteSpace(Endpoint)}; UsernameSet={!string.IsNullOrWhiteSpace(Username)}; WalletNameSet={!string.IsNullOrWhiteSpace(WalletName)}; WalletPassphraseSet={!string.IsNullOrEmpty(WalletPassphrase)}; WalletUnlockSeconds={WalletUnlockSeconds}; LockWalletAfterSend={LockWalletAfterSend}; RequestTimeoutSeconds={RequestTimeoutSeconds}";
    }

    public override string ToString()
    {
        return ToSafeSummary();
    }
}

public class BitcoinJsonRpcTransportRequest
{
    public string Endpoint { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string WalletName { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string ParamsJson { get; init; } = "[]";
    public string RequestId { get; init; } = string.Empty;
    public int RequestTimeoutSeconds { get; init; }

    public string ToSafeSummary()
    {
        return
            $"EndpointSet={!string.IsNullOrWhiteSpace(Endpoint)}; UsernameSet={!string.IsNullOrWhiteSpace(Username)}; WalletNameSet={!string.IsNullOrWhiteSpace(WalletName)}; Method={Method}; ParamsSet={!string.IsNullOrWhiteSpace(ParamsJson)}; RequestId={RequestId}; RequestTimeoutSeconds={RequestTimeoutSeconds}";
    }

    public override string ToString()
    {
        return ToSafeSummary();
    }
}

public class BitcoinJsonRpcTransportResponse
{
    public string Body { get; init; } = string.Empty;
    public int? StatusCode { get; init; }
    public bool? IsSuccess { get; init; }

    public string ToSafeSummary()
    {
        return
            $"StatusCode={(StatusCode.HasValue ? StatusCode.Value.ToString() : string.Empty)}; IsSuccess={(IsSuccess.HasValue ? IsSuccess.Value.ToString() : string.Empty)}; BodySet={!string.IsNullOrWhiteSpace(Body)}";
    }

    public override string ToString()
    {
        return ToSafeSummary();
    }
}

public class BitcoinJsonRpcPayoutClient : IBitcoinPayoutRpcClient
{
    public const string InvalidRequestErrorCode = "bitcoin_jsonrpc_invalid_request";
    public const string InvalidRecipientErrorCode = "bitcoin_jsonrpc_invalid_recipient";
    public const string InvalidAmountErrorCode = "bitcoin_jsonrpc_invalid_amount";
    public const string JsonRpcErrorCode = "bitcoin_jsonrpc_error";
    public const string InvalidResponseErrorCode = "bitcoin_jsonrpc_invalid_response";
    public const string WalletLockedErrorCode = "bitcoin_wallet_locked";
    public const string WalletUnlockFailedErrorCode = "bitcoin_wallet_unlock_failed";
    public const string WalletUnlockAmbiguousErrorCode = "bitcoin_wallet_unlock_ambiguous";
    private const int WalletLockedJsonRpcErrorCode = -13;
    private const int MaxWalletUnlockSeconds = 3600;

    public BitcoinJsonRpcPayoutClient(IBitcoinJsonRpcTransport transport, BitcoinJsonRpcRouteOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = ValidateOptions(options);
    }

    private readonly IBitcoinJsonRpcTransport transport;
    private readonly BitcoinJsonRpcRouteOptions options;
    private long requestId;

    public async Task<BitcoinPayoutRpcResult> SendManyAsync(BitcoinPayoutSendManyRequest request, CancellationToken ct)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(request.Recipients == null || request.Recipients.Count == 0)
            return BitcoinPayoutRpcResult.FailedPreAccept(InvalidRecipientErrorCode,
                "Bitcoin JSON-RPC sendmany requires at least one recipient");

        foreach(var recipient in request.Recipients)
        {
            if(string.IsNullOrWhiteSpace(recipient.Key))
                return BitcoinPayoutRpcResult.FailedPreAccept(InvalidRecipientErrorCode,
                    "Bitcoin JSON-RPC sendmany requires non-empty recipient addresses");

            if(recipient.Value <= 0m)
                return BitcoinPayoutRpcResult.FailedPreAccept(InvalidAmountErrorCode,
                    "Bitcoin JSON-RPC sendmany requires positive recipient amounts");
        }

        var recipients = new ReadOnlyDictionary<string, decimal>(
            request.Recipients.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
        var requestFactory = () => CreateTransportRequest("sendmany", new object[] { "", recipients });
        var response = await transport.SendAsync(requestFactory(), ct);

        return await MapSendResponseWithOptionalUnlockAsync(response, requestFactory, ct);
    }

    public async Task<BitcoinPayoutRpcResult> SendToAddressAsync(BitcoinPayoutSendToAddressRequest request,
        CancellationToken ct)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(string.IsNullOrWhiteSpace(request.Address))
            return BitcoinPayoutRpcResult.FailedPreAccept(InvalidRecipientErrorCode,
                "Bitcoin JSON-RPC sendtoaddress requires a non-empty recipient address");

        if(request.Amount <= 0m)
            return BitcoinPayoutRpcResult.FailedPreAccept(InvalidAmountErrorCode,
                "Bitcoin JSON-RPC sendtoaddress requires a positive amount");

        var requestFactory = () => CreateTransportRequest("sendtoaddress",
            new object[] { request.Address, request.Amount });
        var response = await transport.SendAsync(requestFactory(), ct);

        return await MapSendResponseWithOptionalUnlockAsync(response, requestFactory, ct);
    }

    private BitcoinJsonRpcTransportRequest CreateTransportRequest(string method, object[] parameters)
    {
        return new BitcoinJsonRpcTransportRequest
        {
            Endpoint = options.Endpoint,
            Username = options.Username,
            Password = options.Password,
            WalletName = options.WalletName,
            Method = method,
            ParamsJson = JsonSerializer.Serialize(parameters),
            RequestId = Interlocked.Increment(ref requestId).ToString(),
            RequestTimeoutSeconds = options.RequestTimeoutSeconds
        };
    }

    private async Task<BitcoinPayoutRpcResult> MapSendResponseWithOptionalUnlockAsync(
        BitcoinJsonRpcTransportResponse response,
        Func<BitcoinJsonRpcTransportRequest> retryRequestFactory,
        CancellationToken ct)
    {
        if(!IsWalletLockedResponse(response))
            return MapSendResponse(response);

        if(string.IsNullOrEmpty(options.WalletPassphrase))
            return BitcoinPayoutRpcResult.FailedPreAccept(WalletLockedErrorCode,
                "Bitcoin wallet is locked and no wallet passphrase is configured");

        var unlockResult = await TryUnlockWalletAsync(ct);
        if(unlockResult != null)
            return unlockResult;

        try
        {
            var retryResponse = await transport.SendAsync(retryRequestFactory(), ct);
            return IsWalletLockedResponse(retryResponse)
                ? BitcoinPayoutRpcResult.FailedPreAccept(WalletLockedErrorCode,
                    "Bitcoin wallet remained locked after one unlock attempt")
                : MapSendResponse(retryResponse);
        }
        finally
        {
            if(options.LockWalletAfterSend)
                await TryLockWalletWithoutChangingResultAsync(ct);
        }
    }

    private async Task<BitcoinPayoutRpcResult> TryUnlockWalletAsync(CancellationToken ct)
    {
        BitcoinJsonRpcTransportResponse unlockResponse;
        try
        {
            unlockResponse = await transport.SendAsync(
                CreateTransportRequest("walletpassphrase",
                    new object[] { options.WalletPassphrase, options.WalletUnlockSeconds }), ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception)
        {
            return AmbiguousWalletUnlock();
        }

        if(IsJsonRpcErrorResponse(unlockResponse))
            return BitcoinPayoutRpcResult.FailedPreAccept(WalletUnlockFailedErrorCode,
                "Bitcoin wallet unlock returned a JSON-RPC error");

        if(!IsJsonRpcNullSuccessResponse(unlockResponse))
            return AmbiguousWalletUnlock();

        return null;
    }

    private async Task TryLockWalletWithoutChangingResultAsync(CancellationToken ct)
    {
        try
        {
            await transport.SendAsync(CreateTransportRequest("walletlock", Array.Empty<object>()), ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception)
        {
            // Wallet lock failures must not hide the already classified send result.
        }
    }

    private static BitcoinPayoutRpcResult MapSendResponse(BitcoinJsonRpcTransportResponse response)
    {
        if(response == null || string.IsNullOrWhiteSpace(response.Body))
            return AmbiguousResponse();

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if(root.ValueKind != JsonValueKind.Object)
                return AmbiguousResponse();

            var hasResult = root.TryGetProperty("result", out var resultElement);
            var hasError = root.TryGetProperty("error", out var errorElement) &&
                           errorElement.ValueKind != JsonValueKind.Null &&
                           errorElement.ValueKind != JsonValueKind.Undefined;

            if(hasError)
            {
                if(hasResult && resultElement.ValueKind != JsonValueKind.Null &&
                   resultElement.ValueKind != JsonValueKind.Undefined)
                    return AmbiguousResponse();

                return BitcoinPayoutRpcResult.FailedPreAccept(JsonRpcErrorCode,
                    CreateJsonRpcErrorMessage(errorElement));
            }

            if(response.IsSuccess == false)
                return AmbiguousResponse();

            if(!hasResult || resultElement.ValueKind != JsonValueKind.String)
                return AmbiguousResponse();

            var txId = resultElement.GetString();
            if(string.IsNullOrWhiteSpace(txId))
                return AmbiguousResponse();

            return BitcoinPayoutRpcResult.Accepted(txId);
        }
        catch(JsonException)
        {
            return AmbiguousResponse();
        }
    }

    private static bool IsWalletLockedResponse(BitcoinJsonRpcTransportResponse response)
    {
        return TryGetJsonRpcErrorCode(response, out var code) && code == WalletLockedJsonRpcErrorCode;
    }

    private static bool IsJsonRpcErrorResponse(BitcoinJsonRpcTransportResponse response)
    {
        if(response == null || string.IsNullOrWhiteSpace(response.Body))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if(root.ValueKind != JsonValueKind.Object)
                return false;

            var hasResult = root.TryGetProperty("result", out var resultElement);
            var hasNonNullResult = hasResult &&
                                   resultElement.ValueKind != JsonValueKind.Null &&
                                   resultElement.ValueKind != JsonValueKind.Undefined;
            if(hasNonNullResult)
                return false;

            return root.TryGetProperty("error", out var errorElement) &&
                   errorElement.ValueKind != JsonValueKind.Null &&
                   errorElement.ValueKind != JsonValueKind.Undefined;
        }
        catch(JsonException)
        {
            return false;
        }
    }

    private static bool TryGetJsonRpcErrorCode(BitcoinJsonRpcTransportResponse response, out int code)
    {
        code = 0;
        if(response == null || string.IsNullOrWhiteSpace(response.Body))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if(root.ValueKind != JsonValueKind.Object)
                return false;

            var hasResult = root.TryGetProperty("result", out var resultElement);
            var hasNonNullResult = hasResult &&
                                   resultElement.ValueKind != JsonValueKind.Null &&
                                   resultElement.ValueKind != JsonValueKind.Undefined;
            if(hasNonNullResult)
                return false;

            if(!root.TryGetProperty("error", out var errorElement) ||
               errorElement.ValueKind == JsonValueKind.Null ||
               errorElement.ValueKind == JsonValueKind.Undefined)
                return false;

            return errorElement.ValueKind == JsonValueKind.Object &&
                   errorElement.TryGetProperty("code", out var codeElement) &&
                   codeElement.TryGetInt32(out code);
        }
        catch(JsonException)
        {
            return false;
        }
    }

    private static bool IsJsonRpcNullSuccessResponse(BitcoinJsonRpcTransportResponse response)
    {
        if(response == null || string.IsNullOrWhiteSpace(response.Body) || response.IsSuccess == false)
            return false;

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if(root.ValueKind != JsonValueKind.Object)
                return false;

            if(root.TryGetProperty("error", out var errorElement) &&
               errorElement.ValueKind != JsonValueKind.Null &&
               errorElement.ValueKind != JsonValueKind.Undefined)
                return false;

            return root.TryGetProperty("result", out var resultElement) &&
                   (resultElement.ValueKind == JsonValueKind.Null ||
                    resultElement.ValueKind == JsonValueKind.Undefined);
        }
        catch(JsonException)
        {
            return false;
        }
    }

    private static string CreateJsonRpcErrorMessage(JsonElement errorElement)
    {
        if(errorElement.ValueKind == JsonValueKind.Object &&
           errorElement.TryGetProperty("code", out var codeElement) &&
           codeElement.TryGetInt32(out var code))
            return $"Bitcoin JSON-RPC returned error code {code}";

        return "Bitcoin JSON-RPC returned an error";
    }

    private static BitcoinPayoutRpcResult AmbiguousResponse()
    {
        return BitcoinPayoutRpcResult.AmbiguousRequiresReview(InvalidResponseErrorCode,
            "Bitcoin JSON-RPC response could not be safely interpreted");
    }

    private static BitcoinPayoutRpcResult AmbiguousWalletUnlock()
    {
        return BitcoinPayoutRpcResult.AmbiguousRequiresReview(WalletUnlockAmbiguousErrorCode,
            "Bitcoin wallet unlock could not be safely completed");
    }

    private static BitcoinJsonRpcRouteOptions ValidateOptions(BitcoinJsonRpcRouteOptions options)
    {
        if(options == null)
            throw new ArgumentNullException(nameof(options));

        RequireText(options.Endpoint, nameof(options.Endpoint));
        RequireText(options.Username, nameof(options.Username));
        RequireText(options.Password, nameof(options.Password));

        if(options.RequestTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.RequestTimeoutSeconds),
                "RequestTimeoutSeconds must be greater than zero");

        if(!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri))
            throw new ArgumentException("Endpoint must be an absolute URI", nameof(options));

        if(!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Endpoint must not include URI userinfo credentials", nameof(options));

        var hasPassphrase = !string.IsNullOrEmpty(options.WalletPassphrase);
        if(hasPassphrase && options.WalletUnlockSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.WalletUnlockSeconds),
                "WalletUnlockSeconds must be greater than zero when wallet passphrase is configured");

        if(!hasPassphrase && options.WalletUnlockSeconds > 0)
            throw new ArgumentException("WalletUnlockSeconds requires a wallet passphrase", nameof(options));

        if(options.WalletUnlockSeconds > MaxWalletUnlockSeconds)
            throw new ArgumentOutOfRangeException(nameof(options.WalletUnlockSeconds),
                "WalletUnlockSeconds exceeds the maximum allowed duration");

        return options;
    }

    private static void RequireText(string value, string name)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required", name);
    }
}
