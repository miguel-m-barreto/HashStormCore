using System.Data;
using Dapper;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Model.Projections;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Persistence.Postgres.Repositories;

public class PayoutReservationRepository : IPayoutReservationRepository
{
    private static readonly string[] ReservedAmountStates =
    {
        PayoutIntentStates.Reserved,
        PayoutIntentStates.Sending,
        PayoutIntentStates.Submitted
    };

    private static readonly string[] ActiveIntentStates =
    {
        PayoutIntentStates.Reserved,
        PayoutIntentStates.Sending,
        PayoutIntentStates.Submitted,
        PayoutIntentStates.AmbiguousRequiresReview
    };

    public async Task<PayoutReservationCandidate[]> GetEligibleCandidatesAsync(IDbConnection con, IDbTransaction tx,
        string poolId, decimal minimumPayment, int maxCandidates, CancellationToken ct,
        IReadOnlyCollection<PayoutRewardRecipientThreshold> rewardRecipientThresholds = null)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        if(string.IsNullOrWhiteSpace(poolId))
            throw new ArgumentException("Pool id is required", nameof(poolId));

        if(minimumPayment < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumPayment), "Minimum payment must be greater than or equal to zero");

        if(maxCandidates <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCandidates), "Maximum candidate count must be greater than zero");

        var rewardThresholds = NormalizeRewardRecipientThresholds(rewardRecipientThresholds, minimumPayment);

        const string query = @"WITH reward_thresholds AS (
                SELECT address, minimumpayment
                FROM unnest(CAST(@rewardaddresses AS text[]), CAST(@rewardthresholds AS numeric[])) AS rt(address, minimumpayment)
            ),
            active_intents AS (
                SELECT poolid, address,
                    COALESCE(SUM(CASE WHEN state = ANY(@reservedstates) THEN amount ELSE 0 END), 0) AS reservedamount,
                    COALESCE(SUM(CASE WHEN state = @ambiguousstate THEN amount ELSE 0 END), 0) AS ambiguousamount,
                    COALESCE(SUM(amount), 0) AS activeamount
                FROM payout_intents
                WHERE poolid = @poolid AND state = ANY(@activeintentstates)
                GROUP BY poolid, address
            )
            SELECT b.poolid AS PoolId,
                b.address AS Address,
                b.amount AS BalanceSnapshotAmount,
                b.updated AS BalanceSnapshotUpdated,
                CASE
                    WHEN rt.address IS NOT NULL THEN rt.minimumpayment
                    ELSE COALESCE(ms.paymentthreshold, @minimumpayment)
                END AS PaymentThreshold,
                COALESCE(ai.reservedamount, 0) AS ReservedAmount,
                COALESCE(ai.ambiguousamount, 0) AS AmbiguousAmount,
                b.amount - COALESCE(ai.activeamount, 0) AS AvailableAmount
            FROM balances b
            LEFT JOIN reward_thresholds rt ON rt.address = b.address
            LEFT JOIN miner_settings ms ON ms.poolid = b.poolid AND ms.address = b.address
            LEFT JOIN active_intents ai ON ai.poolid = b.poolid AND ai.address = b.address
            WHERE b.poolid = @poolid
              AND b.amount > 0
              AND COALESCE(ai.activeamount, 0) = 0
              AND b.amount - COALESCE(ai.activeamount, 0) >= CASE
                    WHEN rt.address IS NOT NULL THEN rt.minimumpayment
                    ELSE COALESCE(ms.paymentthreshold, @minimumpayment)
                  END
            ORDER BY b.updated, b.address
            LIMIT @maxcandidates
            FOR UPDATE OF b SKIP LOCKED";

        var entities = await con.QueryAsync<Entities.PayoutReservationCandidate>(new CommandDefinition(query, new
        {
            poolid = poolId,
            minimumpayment = minimumPayment,
            maxcandidates = maxCandidates,
            rewardaddresses = rewardThresholds.Select(x => x.Address).ToArray(),
            rewardthresholds = rewardThresholds.Select(x => x.MinimumPayment.Value).ToArray(),
            reservedstates = ReservedAmountStates,
            activeintentstates = ActiveIntentStates,
            ambiguousstate = PayoutIntentStates.AmbiguousRequiresReview
        }, tx, cancellationToken: ct));

        return entities.Select(MapCandidate).ToArray();
    }

    private static PayoutRewardRecipientThreshold[] NormalizeRewardRecipientThresholds(
        IReadOnlyCollection<PayoutRewardRecipientThreshold> rewardRecipientThresholds, decimal minimumPayment)
    {
        if(rewardRecipientThresholds == null || rewardRecipientThresholds.Count == 0)
            return Array.Empty<PayoutRewardRecipientThreshold>();

        var normalized = rewardRecipientThresholds
            .Select(x =>
            {
                if(x == null)
                    throw new ArgumentException("Reward recipient threshold entry is required", nameof(rewardRecipientThresholds));

                if(string.IsNullOrWhiteSpace(x.Address))
                    throw new ArgumentException("Reward recipient threshold address is required", nameof(rewardRecipientThresholds));

                if(x.MinimumPayment.HasValue && x.MinimumPayment.Value < 0)
                    throw new ArgumentOutOfRangeException(nameof(rewardRecipientThresholds),
                        "Reward recipient minimum payment must be greater than or equal to zero");

                return new PayoutRewardRecipientThreshold
                {
                    Address = x.Address,
                    MinimumPayment = x.MinimumPayment ?? minimumPayment
                };
            })
            .ToArray();

        var duplicate = normalized
            .GroupBy(x => x.Address, StringComparer.Ordinal)
            .FirstOrDefault(x => x.Count() > 1);
        if(duplicate != null)
            throw new ArgumentException($"Duplicate reward recipient threshold address '{duplicate.Key}'", nameof(rewardRecipientThresholds));

        return normalized;
    }

    private static PayoutReservationCandidate MapCandidate(Entities.PayoutReservationCandidate entity)
    {
        return new PayoutReservationCandidate
        {
            PoolId = entity.PoolId,
            Address = entity.Address,
            BalanceSnapshotAmount = entity.BalanceSnapshotAmount,
            BalanceSnapshotUpdated = entity.BalanceSnapshotUpdated,
            PaymentThreshold = entity.PaymentThreshold,
            ReservedAmount = entity.ReservedAmount,
            AmbiguousAmount = entity.AmbiguousAmount,
            AvailableAmount = entity.AvailableAmount
        };
    }

    private static IDbConnection RequireConnection(IDbConnection con)
    {
        return con ?? throw new ArgumentNullException(nameof(con));
    }

    private static IDbTransaction RequireTransaction(IDbTransaction tx)
    {
        return tx ?? throw new ArgumentNullException(nameof(tx));
    }
}
