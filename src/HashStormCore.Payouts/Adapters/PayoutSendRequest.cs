using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Payouts.Adapters;

public record PayoutSendRequest
{
    public string PoolId { get; init; } = string.Empty;
    public string CoinKey { get; init; } = string.Empty;
    public PayoutProfile Profile { get; init; }
    public IReadOnlyCollection<PayoutSendRecipient> Recipients { get; init; } = Array.Empty<PayoutSendRecipient>();
}

public record PayoutSendRecipient
{
    public string Address { get; init; } = string.Empty;
    public decimal Amount { get; init; }
}
