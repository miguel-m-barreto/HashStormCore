using System.Data;
using HashStormCore.Persistence.Model;

namespace HashStormCore.Persistence.Repositories;

public interface IPayoutSettlementRepository
{
    Task<PayoutSettlementAttemptCandidate[]> GetAcceptedAttemptsForSettlementAsync(IDbConnection con, IDbTransaction tx,
        string poolId, int limit, CancellationToken ct);

    Task<PayoutSettlementResult> SettleAcceptedAttemptAsync(IDbConnection con, IDbTransaction tx,
        PayoutSettlementRequest request, CancellationToken ct);
}
