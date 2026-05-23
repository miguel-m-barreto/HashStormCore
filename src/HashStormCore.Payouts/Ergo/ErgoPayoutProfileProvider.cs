using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Ergo;

public class ErgoPayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Ergo);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.ErgoWalletApi,
            PayoutProfileConstants.SendShapes.BatchMultiRecipient,
            PayoutProfileConstants.SendMethods.WalletPaymentTransactionGenerateAndSend,
            PayoutProfileConstants.SettlementEvidenceKinds.TxId) with
        {
            SupportsTransparentTxId = true,
            AllowsBatchMultiRecipient = true,
            RequiresWalletDaemon = true,
            ReservationReady = true
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
