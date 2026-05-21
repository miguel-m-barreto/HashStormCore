using System.Data;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class PayoutAmbiguousReviewService
{
    public PayoutAmbiguousReviewService(IPayoutIntentRepository payoutIntentRepo)
    {
        this.payoutIntentRepo = payoutIntentRepo ?? throw new ArgumentNullException(nameof(payoutIntentRepo));
    }

    private readonly IPayoutIntentRepository payoutIntentRepo;

    public async Task<PayoutAmbiguousReviewResult> ReviewAmbiguousAttemptAsync(IDbConnection con, IDbTransaction tx,
        PayoutAmbiguousReviewRequest request, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);
        ValidateRequest(request);

        switch(request.Decision)
        {
            case PayoutAmbiguousReviewDecision.AcceptedWithEvidence:
                if(request.Evidence == null)
                    throw new ArgumentException("Reviewed accepted ambiguous payout requires external evidence",
                        nameof(request.Evidence));

                var accepted = await payoutIntentRepo.MarkAmbiguousAttemptAcceptedAfterReviewAsync(con, tx,
                    request.BatchId, request.AttemptId, request.PoolId, request.Evidence, request.ReviewedAt, ct);

                return accepted
                    ? PayoutAmbiguousReviewResult.Accepted(request.BatchId, request.AttemptId)
                    : PayoutAmbiguousReviewResult.AttemptNotEligible(request.BatchId, request.AttemptId);

            case PayoutAmbiguousReviewDecision.ProvenNoAccept:
                ValidateError(request.ErrorCode, request.ErrorMessage);

                var attempt = await payoutIntentRepo.GetSendAttemptForExecutionAsync(con, tx, request.AttemptId,
                    request.PoolId, ct);
                if(attempt == null || attempt.BatchId != request.BatchId)
                    return PayoutAmbiguousReviewResult.AttemptNotEligible(request.BatchId, request.AttemptId);

                var failedNoAccept = await payoutIntentRepo.MarkAttemptFailedNoAcceptAsync(con, tx, request.AttemptId,
                    request.PoolId, request.ErrorCode, request.ErrorMessage, request.ReviewedAt, ct);

                return failedNoAccept
                    ? PayoutAmbiguousReviewResult.FailedNoAccept(request.BatchId, request.AttemptId)
                    : PayoutAmbiguousReviewResult.AttemptNotEligible(request.BatchId, request.AttemptId);

            default:
                throw new ArgumentOutOfRangeException(nameof(request.Decision), "Unsupported ambiguous payout review decision");
        }
    }

    private static void ValidateRequest(PayoutAmbiguousReviewRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        RequireText(request.PoolId, nameof(request.PoolId));

        if(request.BatchId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.BatchId), "Payout batch id must be greater than zero");

        if(request.AttemptId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.AttemptId), "Payout send attempt id must be greater than zero");

        if(!Enum.IsDefined(typeof(PayoutAmbiguousReviewDecision), request.Decision))
            throw new ArgumentOutOfRangeException(nameof(request.Decision), "Unsupported ambiguous payout review decision");
    }

#nullable enable annotations
    private static void ValidateError(string? errorCode, string? errorMessage)
    {
        if(string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("Reviewed no-accept payout requires a non-empty error code", nameof(errorCode));

        if(errorMessage != null && string.IsNullOrWhiteSpace(errorMessage))
            throw new ArgumentException("Reviewed no-accept error message cannot be whitespace", nameof(errorMessage));
    }
#nullable restore

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
