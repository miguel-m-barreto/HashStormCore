using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Alephium;

public class AlephiumPayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Alephium);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.AlephiumWalletApi,
            PayoutProfileConstants.SendShapes.AddressGroup,
            PayoutProfileConstants.SendMethods.BuildSignSubmit,
            PayoutProfileConstants.SettlementEvidenceKinds.TxId) with
        {
            SupportsTransparentTxId = true,
            AllowsBatchMultiRecipient = true,
            RequiresWalletDaemon = true,
            AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.AlephiumGroupAware,
            // Planning safety page-size. Execution must still validate gas per attempt and must
            // never silently remove recipients from a prepared attempt.
            MaxRecipientsPerAttempt = 64,
            AddressGroupCount = 4,
            ReservationReady = true
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
