using HashStormCore.Extensions;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class DbPayoutOperationIdReconciliationRunner : IPayoutOperationIdReconciliationRunner
{
    public DbPayoutOperationIdReconciliationRunner(IConnectionFactory connectionFactory,
        IPayoutIntentRepository payoutIntentRepository, IPayoutProfileResolver payoutProfileResolver,
        IPayoutOperationStatusProviderRegistry providerRegistry,
        PayoutOperationIdReconciliationService payoutOperationIdReconciliationService)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.payoutIntentRepository = payoutIntentRepository ??
            throw new ArgumentNullException(nameof(payoutIntentRepository));
        this.payoutProfileResolver = payoutProfileResolver ??
            throw new ArgumentNullException(nameof(payoutProfileResolver));
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        this.payoutOperationIdReconciliationService = payoutOperationIdReconciliationService ??
            throw new ArgumentNullException(nameof(payoutOperationIdReconciliationService));
        evidenceValidator = new PayoutExecutionEvidenceValidator();
    }

    private readonly IConnectionFactory connectionFactory;
    private readonly IPayoutIntentRepository payoutIntentRepository;
    private readonly IPayoutProfileResolver payoutProfileResolver;
    private readonly IPayoutOperationStatusProviderRegistry providerRegistry;
    private readonly PayoutOperationIdReconciliationService payoutOperationIdReconciliationService;
    private readonly PayoutExecutionEvidenceValidator evidenceValidator;

    public async Task<PayoutOperationIdReconciliationRunnerResult> ReconcileOperationIdsAsync(
        PayoutOperationIdReconciliationRunnerRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateRequest(request);

        var candidates = await connectionFactory.RunTx((con, tx) =>
            payoutIntentRepository.GetAttemptsWithOperationIdAsync(con, tx, request.PoolId, request.Limit, ct));

        var pendingCount = 0;
        var attachedCount = 0;
        var alreadyAttachedCount = 0;
        var needsReviewCount = 0;
        var providerErrorCount = 0;
        var attachedAttemptIds = new List<long>();
        var skipped = new List<PayoutOperationIdReconciliationSkippedAttempt>();
        var failures = new List<PayoutOperationIdReconciliationAttemptFailure>();

        foreach(var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var resolution = payoutProfileResolver.Resolve(candidate.Coin);
                if(!resolution.HasProfile)
                {
                    skipped.Add(CreateSkipped(candidate, $"profile resolution failed: {resolution.Reason}"));
                    continue;
                }

                var profile = resolution.Profile;
                if(!profile.ReservationReady)
                {
                    skipped.Add(CreateSkipped(candidate,
                        string.IsNullOrWhiteSpace(profile.NotReadyReason)
                            ? "profile is not reservation-ready"
                            : profile.NotReadyReason));
                    continue;
                }

                if(!evidenceValidator.IsOperationIdReconciliationEligibleByProfile(profile))
                {
                    skipped.Add(CreateSkipped(candidate, "profile is not eligible for operation-id reconciliation"));
                    continue;
                }

                var mismatchReason = GetProfileMetadataMismatchReason(candidate, profile);
                if(mismatchReason != null)
                {
                    skipped.Add(CreateSkipped(candidate, mismatchReason));
                    continue;
                }

                if(!providerRegistry.TryGetProvider(profile, out var provider))
                {
                    skipped.Add(CreateSkipped(candidate,
                        "no registered operation status provider for exact profile key"));
                    continue;
                }

                var result = await payoutOperationIdReconciliationService.ReconcileOperationIdCandidateAsync(candidate,
                    request.CheckedAt, provider, ct);
                pendingCount += result.ProviderPendingCount;
                attachedCount += result.EvidenceAttachedCount;
                alreadyAttachedCount += result.AlreadyAttachedCount;
                needsReviewCount += result.NeedsReviewCount;
                providerErrorCount += result.ProviderErrorCount;
                attachedAttemptIds.AddRange(result.EvidenceAttachedAttemptIds);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested)
            {
                throw;
            }
            catch(Exception ex)
            {
                failures.Add(CreateFailure(candidate, ex));
            }
        }

        return new PayoutOperationIdReconciliationRunnerResult
        {
            CandidateCount = candidates.Length,
            ProviderPendingCount = pendingCount,
            EvidenceAttachedCount = attachedCount,
            AlreadyAttachedCount = alreadyAttachedCount,
            NeedsReviewCount = needsReviewCount,
            ProviderErrorCount = providerErrorCount,
            SkippedCount = skipped.Count,
            FailureCount = failures.Count,
            EvidenceAttachedAttemptIds = attachedAttemptIds,
            SkippedAttempts = skipped,
            Failures = failures
        };
    }

    private static string GetProfileMetadataMismatchReason(PayoutReconciliationAttemptSummary candidate,
        PayoutProfile profile)
    {
        if(!string.Equals(candidate.CoinFamily, profile.CoinFamily, StringComparison.Ordinal))
            return $"coinFamily mismatch: candidate '{candidate.CoinFamily}', resolved '{profile.CoinFamily}'";

        if(!string.Equals(candidate.Handler, profile.AdapterId, StringComparison.Ordinal))
            return $"handler mismatch: candidate '{candidate.Handler}', resolved '{profile.AdapterId}'";

        if(!string.Equals(candidate.SendShape, profile.SendShape, StringComparison.Ordinal))
            return $"sendShape mismatch: candidate '{candidate.SendShape}', resolved '{profile.SendShape}'";

        if(!string.Equals(candidate.Method, profile.SendMethod, StringComparison.Ordinal))
            return $"method mismatch: candidate '{candidate.Method}', resolved '{profile.SendMethod}'";

        return null;
    }

    private static PayoutOperationIdReconciliationSkippedAttempt CreateSkipped(
        PayoutReconciliationAttemptSummary candidate, string reason)
    {
        return new PayoutOperationIdReconciliationSkippedAttempt
        {
            BatchId = candidate.BatchId,
            AttemptId = candidate.AttemptId,
            PoolId = candidate.PoolId,
            Coin = candidate.Coin,
            CoinFamily = candidate.CoinFamily,
            Handler = candidate.Handler,
            SendShape = candidate.SendShape,
            Method = candidate.Method,
            Reason = reason
        };
    }

    private static PayoutOperationIdReconciliationAttemptFailure CreateFailure(
        PayoutReconciliationAttemptSummary candidate, Exception ex)
    {
        return new PayoutOperationIdReconciliationAttemptFailure
        {
            BatchId = candidate.BatchId,
            AttemptId = candidate.AttemptId,
            PoolId = candidate.PoolId,
            Coin = candidate.Coin,
            CoinFamily = candidate.CoinFamily,
            Handler = candidate.Handler,
            SendShape = candidate.SendShape,
            Method = candidate.Method,
            ErrorType = ex.GetType().Name
        };
    }

    private static void ValidateRequest(PayoutOperationIdReconciliationRunnerRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(string.IsNullOrWhiteSpace(request.PoolId))
            throw new ArgumentException("Pool id is required", nameof(request));

        if(request.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Limit),
                "Operation-id reconciliation candidate limit must be greater than zero");

        if(request.CheckedAt == default)
            throw new ArgumentException("CheckedAt must be set", nameof(request));
    }
}
