using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Conceal;

public class ConcealPayoutProfileProvider : IPayoutProfileProvider
{
    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Conceal);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.ConcealWalletRpc,
            PayoutProfileConstants.SendShapes.AddressGroup,
            PayoutProfileConstants.SendMethods.SendTransaction,
            PayoutProfileConstants.SettlementEvidenceKinds.RawHash) with
        {
            AllowsBatchMultiRecipient = true,
            MaxRecipientsPerAttempt = 15,
            RequiresWalletDaemon = true,
            ReservationReady = true
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
