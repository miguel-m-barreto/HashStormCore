using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Xelis;

public class XelisPayoutProfileProvider : IPayoutProfileProvider
{
    // Matches legacy XelisConstants.MaximumDestinationPerTransfer.
    private const int MaximumDestinationPerTransfer = 255;

    public bool CanResolve(CoinDescriptor coin)
    {
        return PayoutProfileProviderHelpers.FamilyEquals(coin, PayoutProfileConstants.Families.Xelis);
    }

    public PayoutProfileResolution Resolve(CoinDescriptor coin)
    {
        var profile = PayoutProfileProviderHelpers.WithCommon(coin,
            PayoutProfileConstants.AdapterIds.XelisWalletRpc,
            PayoutProfileConstants.SendShapes.AddressGroup,
            PayoutProfileConstants.SendMethods.BuildTransaction,
            PayoutProfileConstants.SettlementEvidenceKinds.RawHash) with
        {
            AllowsBatchMultiRecipient = true,
            MaxRecipientsPerAttempt = MaximumDestinationPerTransfer,
            RequiresWalletDaemon = true,
            ReservationReady = true
        };

        return PayoutProfileResolution.Resolved(profile);
    }
}
