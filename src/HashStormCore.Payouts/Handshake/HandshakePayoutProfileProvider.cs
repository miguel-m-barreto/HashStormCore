using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Handshake;

public class HandshakePayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Handshake);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.HandshakeWalletRpc,
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
