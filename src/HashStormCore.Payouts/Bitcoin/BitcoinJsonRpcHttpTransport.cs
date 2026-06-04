using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HashStormCore.Payouts.Bitcoin;

public class BitcoinJsonRpcHttpTransport : IBitcoinJsonRpcTransport
{
    public BitcoinJsonRpcHttpTransport(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    private readonly HttpClient httpClient;

    public async Task<BitcoinJsonRpcTransportResponse> SendAsync(BitcoinJsonRpcTransportRequest request,
        CancellationToken ct)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        ValidateRequest(request);

        using var timeoutCts = request.RequestTimeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;

        if(timeoutCts != null)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.RequestTimeoutSeconds));

        var effectiveToken = timeoutCts?.Token ?? ct;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(request));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", CreateBasicAuthToken(request));
        httpRequest.Content = new StringContent(CreateJsonRpcBody(request), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(httpRequest, effectiveToken);
        var body = response.Content == null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(effectiveToken);

        return new BitcoinJsonRpcTransportResponse
        {
            Body = body,
            StatusCode = (int)response.StatusCode,
            IsSuccess = response.IsSuccessStatusCode
        };
    }

    public override string ToString()
    {
        return "BitcoinJsonRpcHttpTransport";
    }

    private static void ValidateRequest(BitcoinJsonRpcTransportRequest request)
    {
        RequireText(request.Endpoint, nameof(request.Endpoint));
        RequireText(request.Username, nameof(request.Username));
        RequireText(request.Password, nameof(request.Password));
        RequireText(request.Method, nameof(request.Method));
        RequireText(request.ParamsJson, nameof(request.ParamsJson));
        RequireText(request.RequestId, nameof(request.RequestId));

        if(request.RequestTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.RequestTimeoutSeconds),
                "RequestTimeoutSeconds must be greater than zero");

        if(!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri))
            throw new ArgumentException("Endpoint must be an absolute URI", nameof(request));

        if(!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Endpoint must not include URI userinfo credentials", nameof(request));
    }

    private static Uri BuildRequestUri(BitcoinJsonRpcTransportRequest request)
    {
        var endpoint = new Uri(request.Endpoint, UriKind.Absolute);
        if(string.IsNullOrWhiteSpace(request.WalletName))
            return endpoint;

        var basePath = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var query = endpoint.Query ?? string.Empty;

        return new Uri($"{basePath}/wallet/{Uri.EscapeDataString(request.WalletName)}{query}", UriKind.Absolute);
    }

    private static string CreateBasicAuthToken(BitcoinJsonRpcTransportRequest request)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{request.Username}:{request.Password}"));
    }

    private static string CreateJsonRpcBody(BitcoinJsonRpcTransportRequest request)
    {
        using var document = JsonDocument.Parse(request.ParamsJson);
        var payload = new Dictionary<string, object>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = request.Method,
            ["params"] = document.RootElement.Clone(),
            ["id"] = request.RequestId
        };

        return JsonSerializer.Serialize(payload);
    }

    private static void RequireText(string value, string name)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required", name);
    }
}
