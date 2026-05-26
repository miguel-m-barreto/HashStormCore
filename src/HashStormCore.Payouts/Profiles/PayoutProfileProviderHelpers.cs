using HashStormCore.Payouts.CoinMetadata;

namespace HashStormCore.Payouts.Profiles;

internal static class PayoutProfileProviderHelpers
{
    public static bool FamilyEquals(CoinDescriptor coin, string family)
    {
        return string.Equals(coin.Family, family, StringComparison.OrdinalIgnoreCase);
    }

    public static string SymbolOrKey(CoinDescriptor coin)
    {
        return string.IsNullOrWhiteSpace(coin.Symbol) ? coin.CoinKey : coin.Symbol;
    }

    public static PayoutProfile WithCommon(CoinDescriptor coin, string adapterId, string sendShape,
        string sendMethod, string settlementEvidenceKind)
    {
        return new PayoutProfile
        {
            CoinKey = coin.CoinKey,
            CoinSymbol = SymbolOrKey(coin),
            CoinFamily = coin.Family,
            AdapterId = adapterId,
            SendShape = sendShape,
            SendMethod = sendMethod,
            SettlementEvidenceKind = settlementEvidenceKind
        };
    }

    public static IReadOnlyCollection<ulong> ReadPrefixes(CoinDescriptor coin, params string[] keys)
    {
        var prefixes = new List<ulong>();

        foreach(var key in keys)
        {
            if(coin.RawExtensionFlags.TryGetValue(key, out var value) &&
               ulong.TryParse(value, out var prefix) &&
               !prefixes.Contains(prefix))
                prefixes.Add(prefix);
        }

        return prefixes;
    }
}
