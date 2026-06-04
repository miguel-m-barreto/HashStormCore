using System;
using System.Net.Http;

namespace HashStormCore.PayoutProcessor.Configuration;

public class DefaultBitcoinJsonRpcHttpClientProvider : IBitcoinJsonRpcHttpClientProvider
{
    public const string ClientName = "hashstorm-bitcoin-jsonrpc";

    public DefaultBitcoinJsonRpcHttpClientProvider(IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    private readonly IHttpClientFactory httpClientFactory;

    public HttpClient GetHttpClient(BitcoinJsonRpcHttpClientRoute route)
    {
        if(route == null)
            throw new ArgumentNullException(nameof(route));

        return httpClientFactory.CreateClient(ClientName);
    }

    public override string ToString()
    {
        return nameof(DefaultBitcoinJsonRpcHttpClientProvider);
    }
}
