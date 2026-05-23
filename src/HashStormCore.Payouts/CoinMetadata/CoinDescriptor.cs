namespace HashStormCore.Payouts.CoinMetadata;

public record CoinDescriptor
{
    public string CoinKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string CanonicalName { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Family { get; init; } = string.Empty;
    public bool HasBrokenSendMany { get; init; }
    public bool UseBitcoinPayoutHandler { get; init; }
    public bool UsesZCashAddressFormat { get; init; }
    public bool HasUsesZCashAddressFormat { get; init; }
    public int? PayoutDecimalPlaces { get; init; }
    public IReadOnlyDictionary<string, string> RawExtensionFlags { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
