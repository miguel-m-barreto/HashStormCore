using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Equihash;

public class EquihashPayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Equihash);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        if(coin.UseBitcoinPayoutHandler)
        {
            var bitcoinProfile = PayoutProfileProviderHelpers.WithCommon(coin,
                PayoutProfileConstants.AdapterIds.EquihashBitcoinRpc,
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

            return PayoutProfileResolution.Resolved(bitcoinProfile);
        }

        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.EquihashZAsync,
            PayoutProfileConstants.SendShapes.AsyncOperation,
            PayoutProfileConstants.SendMethods.ZSendMany,
            PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId) with
        {
            RequiresOperationIdProvider = true,
            SupportsShieldedOperationTracking = true,
            AllowsBatchMultiRecipient = true,
            RequiresWalletDaemon = true,
            ReservationReady = false,
            NotReadyReason = "Equihash shielded payout execution requires an operation-id provider to attach txid evidence before settlement"
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
