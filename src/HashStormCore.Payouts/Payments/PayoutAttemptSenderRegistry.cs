using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payments;

public record PayoutAttemptSenderKey
{
    public string CoinFamily { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
    public string SendShape { get; init; } = string.Empty;
    public string SendMethod { get; init; } = string.Empty;
}

public record PayoutAttemptSenderRegistration
{
    public PayoutAttemptSenderKey Key { get; init; }
    public IPayoutAttemptSender Sender { get; init; }
}

public interface IPayoutAttemptSenderRegistry
{
    bool TryGetSender(PayoutProfile profile, out IPayoutAttemptSender sender);
}

public class PayoutAttemptSenderRegistry : IPayoutAttemptSenderRegistry
{
    public PayoutAttemptSenderRegistry(IEnumerable<PayoutAttemptSenderRegistration> registrations)
    {
        if(registrations == null)
            throw new ArgumentNullException(nameof(registrations));

        foreach(var registration in registrations)
        {
            if(registration == null)
                throw new ArgumentException("Payout sender registration cannot be null", nameof(registrations));

            ValidateKey(registration.Key, nameof(registration.Key));

            if(registration.Sender == null)
                throw new ArgumentNullException(nameof(registration.Sender));

            if(!senders.TryAdd(registration.Key, registration.Sender))
                throw new ArgumentException("Duplicate payout sender registration key", nameof(registrations));
        }
    }

    private readonly Dictionary<PayoutAttemptSenderKey, IPayoutAttemptSender> senders = new();

    public bool TryGetSender(PayoutProfile profile, out IPayoutAttemptSender sender)
    {
        if(profile == null)
            throw new ArgumentNullException(nameof(profile));

        sender = null;

        if(!HasProfileKey(profile))
            return false;

        return senders.TryGetValue(new PayoutAttemptSenderKey
        {
            CoinFamily = profile.CoinFamily,
            AdapterId = profile.AdapterId,
            SendShape = profile.SendShape,
            SendMethod = profile.SendMethod
        }, out sender);
    }

    private static bool HasProfileKey(PayoutProfile profile)
    {
        return !string.IsNullOrWhiteSpace(profile.CoinFamily) &&
               !string.IsNullOrWhiteSpace(profile.AdapterId) &&
               !string.IsNullOrWhiteSpace(profile.SendShape) &&
               !string.IsNullOrWhiteSpace(profile.SendMethod);
    }

    private static void ValidateKey(PayoutAttemptSenderKey key, string name)
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
