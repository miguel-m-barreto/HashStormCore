using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Ethereum;

public class EthereumPayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Ethereum);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.EthereumRpc,
            PayoutProfileConstants.SendShapes.PerAddress,
            PayoutProfileConstants.SendMethods.EthSendTransaction,
            PayoutProfileConstants.SettlementEvidenceKinds.TxId) with
        {
            SupportsTransparentTxId = true,
            AllowsPerAddress = true,
            ReservationReady = true
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
