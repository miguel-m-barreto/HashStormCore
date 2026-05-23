using HashStormCore.Payouts.CoinMetadata;

namespace HashStormCore.Payouts.Profiles;

public class PayoutProfileResolver : IPayoutProfileResolver
{
    public PayoutProfileResolver(ICoinMetadataRegistry coinMetadataRegistry,
        IEnumerable<IPayoutProfileProvider> providers)
    {
        this.coinMetadataRegistry = coinMetadataRegistry ?? throw new ArgumentNullException(nameof(coinMetadataRegistry));
        this.providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
    }

    private readonly ICoinMetadataRegistry coinMetadataRegistry;
    private readonly IPayoutProfileProvider[] providers;

    public PayoutProfileResolution Resolve(string coinKey)
    {
        if(string.IsNullOrWhiteSpace(coinKey))
            return PayoutProfileResolution.Unsupported("Pool coin is required");

        if(!coinMetadataRegistry.TryGetCoin(coinKey, out var coin))
            return PayoutProfileResolution.Unsupported($"Coin '{coinKey.Trim()}' was not found in coins.json");

        if(string.IsNullOrWhiteSpace(coin.Family))
            return PayoutProfileResolution.Unsupported($"Coin '{coin.CoinKey}' has missing family metadata");

        var matches = providers.Where(x => x.CanResolve(coin)).ToArray();

        if(matches.Length == 0)
            return PayoutProfileResolution.Unsupported(
                $"Coin '{coin.CoinKey}' uses unsupported payout family '{coin.Family}'");

        if(matches.Length > 1)
            return PayoutProfileResolution.Unsupported(
                $"Coin '{coin.CoinKey}' has ambiguous payout profile providers for family '{coin.Family}'");

        var resolution = matches[0].Resolve(coin);

        if(resolution == null || (!resolution.HasProfile && resolution.Status != PayoutProfileResolutionStatus.Unsupported))
            return PayoutProfileResolution.Unsupported($"Coin '{coin.CoinKey}' did not resolve to a payout profile");

        if(resolution.HasProfile &&
           resolution.Profile.ReservationReady &&
           string.Equals(resolution.Profile.SendShape, PayoutProfileConstants.SendShapes.AddressGroup,
               StringComparison.Ordinal) &&
           (!resolution.Profile.MaxRecipientsPerAttempt.HasValue ||
            resolution.Profile.MaxRecipientsPerAttempt.Value <= 0))
        {
            return PayoutProfileResolution.NotReady(resolution.Profile,
                "Address-group payout planning requires MaxRecipientsPerAttempt greater than zero before reservation is enabled");
        }

        return resolution;
    }
}
