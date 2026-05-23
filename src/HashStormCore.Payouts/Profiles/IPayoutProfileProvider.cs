using HashStormCore.Payouts.CoinMetadata;

namespace HashStormCore.Payouts.Profiles;

public interface IPayoutProfileProvider
{
    bool CanResolve(CoinDescriptor coin);

    PayoutProfileResolution Resolve(CoinDescriptor coin);
}
