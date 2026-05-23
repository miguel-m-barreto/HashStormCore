using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Cryptonote;

public class CryptonotePayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Cryptonote);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.CryptonoteWalletRpc,
            PayoutProfileConstants.SendShapes.AddressGroup,
            PayoutProfileConstants.SendMethods.TransferSplit,
            PayoutProfileConstants.SettlementEvidenceKinds.RawHash) with
        {
            AllowsBatchMultiRecipient = true,
            MaxRecipientsPerAttempt = 15,
            MayReturnMultipleTransactionHashes = true,
            RequiresPerIntentEvidenceMapping = true,
            RequiresWalletDaemon = true,
            ReservationReady = false,
            NotReadyReason = "Cryptonote transfer_split can return multiple transaction hashes; per-intent evidence mapping must be explicit before reservation is enabled"
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
