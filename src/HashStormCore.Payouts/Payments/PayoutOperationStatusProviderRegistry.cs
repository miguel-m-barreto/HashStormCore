using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payments;

public record PayoutOperationStatusProviderKey
{
    public string CoinFamily { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
    public string SendShape { get; init; } = string.Empty;
    public string SendMethod { get; init; } = string.Empty;
}

public record PayoutOperationStatusProviderRegistration
{
    public PayoutOperationStatusProviderKey Key { get; init; }
    public IPayoutOperationStatusProvider Provider { get; init; }
}

public interface IPayoutOperationStatusProviderRegistry
{
    bool TryGetProvider(PayoutProfile profile, out IPayoutOperationStatusProvider provider);
}

public class PayoutOperationStatusProviderRegistry : IPayoutOperationStatusProviderRegistry
{
    public PayoutOperationStatusProviderRegistry(IEnumerable<PayoutOperationStatusProviderRegistration> registrations)
    {
        if(registrations == null)
            throw new ArgumentNullException(nameof(registrations));

        foreach(var registration in registrations)
        {
            if(registration == null)
                throw new ArgumentException("Operation status provider registration cannot be null",
                    nameof(registrations));

            ValidateKey(registration.Key, nameof(registration.Key));

            if(registration.Provider == null)
                throw new ArgumentNullException(nameof(registration.Provider));

            if(!providers.TryAdd(registration.Key, registration.Provider))
                throw new ArgumentException("Duplicate operation status provider registration key",
                    nameof(registrations));
        }
    }

    private readonly Dictionary<PayoutOperationStatusProviderKey, IPayoutOperationStatusProvider> providers = new();

    public bool TryGetProvider(PayoutProfile profile, out IPayoutOperationStatusProvider provider)
    {
        if(profile == null)
            throw new ArgumentNullException(nameof(profile));

        provider = null;

        if(!HasProfileKey(profile))
            return false;

        return providers.TryGetValue(new PayoutOperationStatusProviderKey
        {
            CoinFamily = profile.CoinFamily,
            AdapterId = profile.AdapterId,
            SendShape = profile.SendShape,
            SendMethod = profile.SendMethod
        }, out provider);
    }

    private static bool HasProfileKey(PayoutProfile profile)
    {
        return !string.IsNullOrWhiteSpace(profile.CoinFamily) &&
               !string.IsNullOrWhiteSpace(profile.AdapterId) &&
               !string.IsNullOrWhiteSpace(profile.SendShape) &&
               !string.IsNullOrWhiteSpace(profile.SendMethod);
    }

    private static void ValidateKey(PayoutOperationStatusProviderKey key, string name)
    {
        if(key == null)
            throw new ArgumentNullException(name);

        RequireText(key.CoinFamily, nameof(key.CoinFamily));
        RequireText(key.AdapterId, nameof(key.AdapterId));
        RequireText(key.SendShape, nameof(key.SendShape));
        RequireText(key.SendMethod, nameof(key.SendMethod));
    }

    private static void RequireText(string value, string name)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required", name);
    }
}
