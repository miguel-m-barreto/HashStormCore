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
            AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.ConcealPaymentIdAware,
            AllowsBatchMultiRecipient = true,
            MaxRecipientsPerAttempt = 15,
            RequiresWalletDaemon = true,
            ReservationReady = true,
            IntegratedAddressPrefixes = ReadIntegratedAddressPrefixes(coin)
        };

        return PayoutProfileResolution.Resolved(profile);
    }

    private static IReadOnlyCollection<ulong> ReadIntegratedAddressPrefixes(CoinDescriptor coin)
    {
        var prefixes = new List<ulong>();
        AddPrefix(prefixes, coin, "addressPrefixIntegrated");
        AddPrefix(prefixes, coin, "addressPrefixIntegratedTestnet");
        return prefixes;
    }

    private static void AddPrefix(ICollection<ulong> prefixes, CoinDescriptor coin, string key)
    {
        if(coin.RawExtensionFlags.TryGetValue(key, out var value) &&
           ulong.TryParse(value, out var prefix) &&
           !prefixes.Contains(prefix))
            prefixes.Add(prefix);
    }
}
