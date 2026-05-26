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
            PayoutProfileConstants.SendMethods.Transfer,
            PayoutProfileConstants.SettlementEvidenceKinds.RawHash) with
        {
            AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.CryptonotePaymentIdAware,
            AllowsBatchMultiRecipient = true,
            MaxRecipientsPerAttempt = 15,
            MayReturnMultipleTransactionHashes = true,
            RequiresSingleEvidencePerAttempt = true,
            MultiHashEvidencePolicy = PayoutProfileConstants.MultiHashEvidencePolicies.Unsupported,
            RequiresWalletDaemon = true,
            ReservationReady = true,
            IntegratedAddressPrefixes = PayoutProfileProviderHelpers.ReadPrefixes(coin,
                "addressPrefixIntegrated",
                "addressPrefixIntegratedTestnet",
                "addressPrefixIntegratedStagenet")
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
