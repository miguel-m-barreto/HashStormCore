using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Beam;

public class BeamPayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Beam);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.BeamWalletRpc,
            PayoutProfileConstants.SendShapes.PerAddress,
            PayoutProfileConstants.SendMethods.BeamSendTransaction,
            PayoutProfileConstants.SettlementEvidenceKinds.TxId) with
        {
            AllowsPerAddress = true,
            RequiresWalletDaemon = true,
            ReservationReady = true
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
