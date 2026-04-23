using System.Net;
using System.Net.Http.Headers;
using HashStormCore.Blockchain.Alephium.Configuration;
using HashStormCore.Configuration;
using HashStormCore.Extensions;
using HashStormCore.Mining;
using NLog;

namespace HashStormCore.Blockchain.Alephium;

public static class AlephiumClientFactory
{
    public static AlephiumClient CreateClient(PoolConfig poolConfig, ClusterConfig clusterConfig, ILogger logger)
    {
        var epConfig = poolConfig.Daemons.First();
        var extra = epConfig.Extra.SafeExtensionDataAs<AlephiumDaemonEndpointConfigExtra>();

        if(logger != null && clusterConfig.PaymentProcessing?.Enabled == true &&
           poolConfig.PaymentProcessing?.Enabled == true && string.IsNullOrEmpty(extra?.ApiKey))
            throw new PoolStartupException("Alephium daemon apiKey not provided", poolConfig.Id);

        var baseUrl = new UriBuilder(epConfig.Ssl || epConfig.Http2 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            epConfig.Host, epConfig.Port, epConfig.HttpPath);

        var result = new AlephiumClient(baseUrl.ToString(), new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip,
        }));

        if(!string.IsNullOrEmpty(extra?.ApiKey))
            result.RequestHeaders["X-API-KEY"] = extra.ApiKey;

        if(!string.IsNullOrEmpty(epConfig.User))
        {
            var auth = $"{epConfig.User}:{epConfig.Password}";
            result.RequestHeaders["Authorization"] = new AuthenticationHeaderValue("Basic", auth.ToByteArrayBase64()).ToString();
        }
#if DEBUG
        result.ReadResponseAsString = true;
#endif
        return result;
    }
}
