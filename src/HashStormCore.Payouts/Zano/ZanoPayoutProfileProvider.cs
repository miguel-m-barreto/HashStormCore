using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Zano;

public class ZanoPayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Zano);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.ZanoWalletRpc,
            PayoutProfileConstants.SendShapes.AddressGroup,
            PayoutProfileConstants.SendMethods.TransferSplit,
            PayoutProfileConstants.SettlementEvidenceKinds.RawHash) with
        {
            AllowsBatchMultiRecipient = true,
            MaxRecipientsPerAttempt = 256,
            MayReturnMultipleTransactionHashes = true,
            RequiresPerIntentEvidenceMapping = true,
            RequiresWalletDaemon = true,
            ReservationReady = false,
            NotReadyReason = "Zano transfer_split can return multiple transaction hashes; per-intent evidence mapping must be explicit before reservation is enabled"
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
