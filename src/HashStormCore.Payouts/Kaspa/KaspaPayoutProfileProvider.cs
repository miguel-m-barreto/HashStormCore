using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Kaspa;

public class KaspaPayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Kaspa);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.KaspaWalletWrapper,
            PayoutProfileConstants.SendShapes.PerAddress,
            PayoutProfileConstants.SendMethods.KaspaSend,
            PayoutProfileConstants.SettlementEvidenceKinds.UnsafePlaceholder) with
        {
            AllowsPerAddress = true,
            RequiresExternalWalletWrapper = true,
            PlaceholderEvidenceUnsafe = true,
            ReservationReady = false,
            NotReadyReason = "Kaspa legacy wrapper may persist placeholder evidence when no real txid is returned"
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
