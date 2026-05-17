using System.Data;
using HashStormCore.Persistence.Model.Projections;

namespace HashStormCore.Persistence.Repositories;

public interface IPayoutReservationRepository
{
    Task<PayoutReservationCandidate[]> GetEligibleCandidatesAsync(IDbConnection con, IDbTransaction tx,
        string poolId, decimal minimumPayment, int maxCandidates, CancellationToken ct);
}
