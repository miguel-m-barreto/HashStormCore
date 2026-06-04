using System.Data;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class PayoutAmbiguousReviewService
{
    public PayoutAmbiguousReviewService(IPayoutIntentRepository payoutIntentRepo,
        IPayoutProfileResolver profileResolver)
    {
        this.payoutIntentRepo = payoutIntentRepo ?? throw new ArgumentNullException(nameof(payoutIntentRepo));
        this.profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        evidenceValidator = new PayoutExecutionEvidenceValidator();
    }

    private readonly IPayoutIntentRepository payoutIntentRepo;
    private readonly IPayoutProfileResolver profileResolver;
    private readonly PayoutExecutionEvidenceValidator evidenceValidator;

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

                if(!await IsAcceptedEvidenceEligibleAsync(con, tx, request, ct))
                    return PayoutAmbiguousReviewResult.AttemptNotEligible(request.BatchId, request.AttemptId);

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

    private async Task<bool> IsAcceptedEvidenceEligibleAsync(IDbConnection con, IDbTransaction tx,
        PayoutAmbiguousReviewRequest request, CancellationToken ct)
    {
        var context = await payoutIntentRepo.GetAmbiguousAttemptReviewContextAsync(con, tx, request.BatchId,
            request.AttemptId, request.PoolId, ct);
        if(context == null)
            return false;

        if(!string.Equals(context.BatchState, PayoutBatchStates.AmbiguousRequiresReview, StringComparison.Ordinal) ||
           !string.Equals(context.AttemptState, PayoutSendAttemptStates.AmbiguousRequiresReview,
               StringComparison.Ordinal))
            return false;

        var resolution = profileResolver.Resolve(context.Coin);
        if(!resolution.HasProfile || !resolution.Profile.ReservationReady)
            return false;

        var profile = resolution.Profile;
        if(!IsReviewContextCompatibleWithProfile(context, profile))
            return false;

        if(!evidenceValidator.TryValidateFinalAcceptedEvidenceForProfile(profile, request.Evidence, out _))
            return false;

        return await HasNoConflictingFinalEvidenceAsync(con, tx, context, request.Evidence, ct);
    }

    private static bool IsReviewContextCompatibleWithProfile(PayoutAmbiguousAttemptReviewContext context,
        PayoutProfile profile)
    {
        return string.Equals(context.CoinFamily, profile.CoinFamily, StringComparison.Ordinal) &&
               string.Equals(context.Handler, profile.AdapterId, StringComparison.Ordinal) &&
               string.Equals(context.SendShape, profile.SendShape, StringComparison.Ordinal) &&
               string.Equals(context.Method, profile.SendMethod, StringComparison.Ordinal);
    }

    private async Task<bool> HasNoConflictingFinalEvidenceAsync(IDbConnection con, IDbTransaction tx,
        PayoutAmbiguousAttemptReviewContext context, PayoutAttemptEvidence evidence, CancellationToken ct)
    {
        var confirmations = await payoutIntentRepo.GetAttemptConfirmationsAsync(con, tx, context.BatchId,
            context.AttemptId, context.PoolId, ct);

        var existingPairs = confirmations
            .Where(x => evidenceValidator.IsFinalSettlementEvidenceKind(x.Kind) &&
                        !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new EvidencePair(x.Kind, x.Value))
            .Distinct()
            .ToArray();

        return existingPairs.Length == 0 ||
               existingPairs.All(x => string.Equals(x.Kind, evidence.Kind, StringComparison.Ordinal) &&
                                      string.Equals(x.Value, evidence.Value, StringComparison.Ordinal));
    }

    private readonly record struct EvidencePair(string Kind, string Value);

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
