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
    public int RequestTimeoutSeconds { get; init; } = 30;

    public string ToSafeSummary()
    {
        return
            $"EndpointSet={!string.IsNullOrWhiteSpace(Endpoint)}; UsernameSet={!string.IsNullOrWhiteSpace(Username)}; WalletName={WalletName}; RequestTimeoutSeconds={RequestTimeoutSeconds}";
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
            $"EndpointSet={!string.IsNullOrWhiteSpace(Endpoint)}; UsernameSet={!string.IsNullOrWhiteSpace(Username)}; WalletName={WalletName}; Method={Method}; ParamsSet={!string.IsNullOrWhiteSpace(ParamsJson)}; RequestId={RequestId}; RequestTimeoutSeconds={RequestTimeoutSeconds}";
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
        var response = await transport.SendAsync(CreateTransportRequest("sendmany", new object[] { "", recipients }),
            ct);

        return MapResponse(response);
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

        var response = await transport.SendAsync(CreateTransportRequest("sendtoaddress",
            new object[] { request.Address, request.Amount }), ct);

        return MapResponse(response);
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

    private static BitcoinPayoutRpcResult MapResponse(BitcoinJsonRpcTransportResponse response)
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

        return options;
    }

    private static void RequireText(string value, string name)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required", name);
    }
}
