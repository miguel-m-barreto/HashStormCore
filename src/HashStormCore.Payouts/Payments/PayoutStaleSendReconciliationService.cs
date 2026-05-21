using System.Data;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class PayoutStaleSendReconciliationService
{
    public PayoutStaleSendReconciliationService(IPayoutIntentRepository payoutIntentRepo)
    {
        this.payoutIntentRepo = payoutIntentRepo ?? throw new ArgumentNullException(nameof(payoutIntentRepo));
    }

    private readonly IPayoutIntentRepository payoutIntentRepo;

    public async Task<PayoutStaleSendReconciliationResult> MarkStaleSendingBatchesAmbiguousAsync(
        IDbConnection con, IDbTransaction tx, PayoutStaleSendReconciliationRequest request, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);
        ValidateRequest(request);

        var candidates = await payoutIntentRepo.GetStaleSendingBatchesForUpdateAsync(con, tx, request.PoolId,
            request.OlderThan, request.Limit, ct);

        var marked = new List<long>();
        var skipped = new List<long>();

        foreach(var candidate in candidates)
        {
            var didMark = await payoutIntentRepo.MarkStaleBatchAmbiguousAsync(con, tx, candidate.BatchId,
                candidate.StaleAttemptId, request.PoolId, request.ErrorCode, request.ErrorMessage,
                request.Updated, ct);

            if(didMark)
                marked.Add(candidate.BatchId);
            else
                skipped.Add(candidate.BatchId);
        }

        return new PayoutStaleSendReconciliationResult
        {
            CandidateBatchCount = candidates.Length,
            MarkedBatchIds = marked,
            SkippedBatchIds = skipped
        };
    }

    private static void ValidateRequest(PayoutStaleSendReconciliationRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        RequireText(request.PoolId, nameof(request.PoolId));
        RequireText(request.ErrorCode, nameof(request.ErrorCode));

        if(request.ErrorMessage != null && string.IsNullOrWhiteSpace(request.ErrorMessage))
            throw new ArgumentException("Error message cannot be whitespace", nameof(request.ErrorMessage));

        if(request.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Limit), "Stale send reconciliation limit must be greater than zero");
    }

    private static void RequireText(string value, string name)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required", name);
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
