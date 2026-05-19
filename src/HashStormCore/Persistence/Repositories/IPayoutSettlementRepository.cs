using System.Data;
using HashStormCore.Persistence.Model;

namespace HashStormCore.Persistence.Repositories;

public interface IPayoutSettlementRepository
{
    Task<PayoutSettlementResult> SettleAcceptedAttemptAsync(IDbConnection con, IDbTransaction tx,
        PayoutSettlementRequest request, CancellationToken ct);
}
