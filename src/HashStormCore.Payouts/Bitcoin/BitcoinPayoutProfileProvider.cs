using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Bitcoin;

public class BitcoinPayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Bitcoin) ||
            PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Nexa) ||
            PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Progpow) ||
            PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Satoshicash);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.BitcoinRpc,
            coin.HasBrokenSendMany
                ? PayoutProfileConstants.SendShapes.PerAddress
                : PayoutProfileConstants.SendShapes.BatchMultiRecipient,
            coin.HasBrokenSendMany
                ? PayoutProfileConstants.SendMethods.SendToAddress
                : PayoutProfileConstants.SendMethods.SendMany,
            PayoutProfileConstants.SettlementEvidenceKinds.TxId) with
        {
            SupportsTransparentTxId = true,
            AllowsBatchMultiRecipient = !coin.HasBrokenSendMany,
            AllowsPerAddress = coin.HasBrokenSendMany,
            RequiresWalletDaemon = true,
            ReservationReady = true
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
