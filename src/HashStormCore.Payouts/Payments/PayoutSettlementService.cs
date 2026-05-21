using System.Data;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class PayoutSettlementService
{
    public PayoutSettlementService(IPayoutSettlementRepository payoutSettlementRepo)
    {
        this.payoutSettlementRepo = payoutSettlementRepo ?? throw new ArgumentNullException(nameof(payoutSettlementRepo));
    }

    private readonly IPayoutSettlementRepository payoutSettlementRepo;

    public Task<PayoutSettlementResult> SettleAcceptedAttemptAsync(IDbConnection con, IDbTransaction tx,
        PayoutSettlementRequest request, CancellationToken ct)
    {
        return payoutSettlementRepo.SettleAcceptedAttemptAsync(con, tx, request, ct);
    }
}
