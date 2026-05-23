namespace HashStormCore.Payouts.Adapters;

public interface IPayoutAdapter
{
    Task<PayoutSendResult> SendAsync(PayoutSendRequest request, CancellationToken ct);
}
