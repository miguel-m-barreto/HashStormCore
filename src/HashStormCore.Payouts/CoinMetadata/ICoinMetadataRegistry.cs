namespace HashStormCore.Payouts.CoinMetadata;

public interface ICoinMetadataRegistry
{
    bool TryGetCoin(string coinKey, out CoinDescriptor descriptor);
}
