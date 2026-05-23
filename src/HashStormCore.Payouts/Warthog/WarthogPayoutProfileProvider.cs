using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Warthog;

public class WarthogPayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Warthog);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.WarthogRestSigned,
            PayoutProfileConstants.SendShapes.PerAddress,
            PayoutProfileConstants.SendMethods.TransactionAdd,
            PayoutProfileConstants.SettlementEvidenceKinds.RawHash) with
        {
            AllowsPerAddress = true,
            RequiresExternalWalletWrapper = true,
            RequiresPrivateKeyMaterial = true,
            ReservationReady = false,
            NotReadyReason = "Warthog adapter private-key handling is not implemented in the sidecar"
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
