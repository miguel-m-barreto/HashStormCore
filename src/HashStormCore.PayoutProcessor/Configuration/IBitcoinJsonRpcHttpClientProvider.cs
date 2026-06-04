using System.Net.Http;

namespace HashStormCore.PayoutProcessor.Configuration;

public interface IBitcoinJsonRpcHttpClientProvider
{
    HttpClient GetHttpClient(BitcoinJsonRpcHttpClientRoute route);
}

public class BitcoinJsonRpcHttpClientRoute
{
    public int ConfigIndex { get; init; }
    public string PoolId { get; init; } = string.Empty;
    public string Coin { get; init; } = string.Empty;
    public string SafeSummary { get; init; } = string.Empty;

    public string ToSafeSummary()
    {
        return
            $"ConfigIndex={ConfigIndex}; PoolId={PoolId}; Coin={Coin}; SafeSummarySet={!string.IsNullOrWhiteSpace(SafeSummary)}";
    }

    public override string ToString()
    {
        return ToSafeSummary();
    }
}
