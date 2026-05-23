namespace HashStormCore.Payouts.Profiles;

public interface IPayoutProfileResolver
{
    PayoutProfileResolution Resolve(string coinKey);
}
