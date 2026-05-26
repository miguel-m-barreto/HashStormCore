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
            PayoutProfileConstants.SettlementEvidenceKinds.TxId) with
        {
            AllowsPerAddress = true,
            RequiresExternalWalletWrapper = true,
            // Future sidecar execution must fail closed if the wrapper/daemon does not return
            // a real non-empty transaction id; never synthesize legacy address/amount evidence.
            ReservationReady = true
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
