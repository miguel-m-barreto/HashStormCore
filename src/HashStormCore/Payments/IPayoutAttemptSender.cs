using HashStormCore.Persistence.Model;

namespace HashStormCore.Payments;

public interface IPayoutAttemptSender
{
    Task<PayoutAttemptSendResult> SendAsync(PayoutSendExecutionContext context, CancellationToken ct);
}
