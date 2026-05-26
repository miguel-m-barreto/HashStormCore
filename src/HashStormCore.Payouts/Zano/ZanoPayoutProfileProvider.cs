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
            PayoutProfileConstants.SendMethods.Transfer,
            PayoutProfileConstants.SettlementEvidenceKinds.RawHash) with
        {
            AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.ZanoPaymentIdAware,
            AllowsBatchMultiRecipient = true,
            MaxRecipientsPerAttempt = 256,
            MayReturnMultipleTransactionHashes = true,
            RequiresSingleEvidencePerAttempt = true,
            MultiHashEvidencePolicy = PayoutProfileConstants.MultiHashEvidencePolicies.Unsupported,
            RequiresWalletDaemon = true,
            ReservationReady = true,
            IntegratedAddressPrefixes = PayoutProfileProviderHelpers.ReadPrefixes(coin,
                "addressPrefixIntegrated",
                "addressPrefixIntegratedTestnet",
                "addressV2PrefixIntegrated",
                "addressV2PrefixIntegratedTestnet",
                "auditableAddressIntegratedPrefix",
                "auditableAddressIntegratedPrefixTestnet")
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
