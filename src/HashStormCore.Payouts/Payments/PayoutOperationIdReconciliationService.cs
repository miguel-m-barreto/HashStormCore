using HashStormCore.Extensions;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class PayoutOperationIdReconciliationService
{
    public PayoutOperationIdReconciliationService(IConnectionFactory cf, IPayoutIntentRepository payoutIntentRepo,
        IPayoutProfileResolver profileResolver)
    {
        this.cf = cf ?? throw new ArgumentNullException(nameof(cf));
        this.payoutIntentRepo = payoutIntentRepo ?? throw new ArgumentNullException(nameof(payoutIntentRepo));
        this.profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        evidenceValidator = new PayoutExecutionEvidenceValidator();
    }

    private readonly IConnectionFactory cf;
    private readonly IPayoutIntentRepository payoutIntentRepo;
    private readonly IPayoutProfileResolver profileResolver;
    private readonly PayoutExecutionEvidenceValidator evidenceValidator;

    public async Task<PayoutOperationIdReconciliationResult> ReconcileOperationIdCandidateAsync(
        PayoutReconciliationAttemptSummary candidate, DateTime checkedAt, IPayoutOperationStatusProvider provider,
        CancellationToken ct)
    {
        if(candidate == null)
            throw new ArgumentNullException(nameof(candidate));

        if(provider == null)
            throw new ArgumentNullException(nameof(provider));

        var operationId = await GetOperationIdAsync(candidate, ct);
        if(string.IsNullOrWhiteSpace(operationId))
            return SingleResult(needsReviewCount: 1);

        if(!IsCandidateEligibleForOperationIdReconciliation(candidate))
            return SingleResult(needsReviewCount: 1);

        PayoutOperationStatusResult providerResult;

        try
        {
            providerResult = await provider.GetOperationStatusAsync(candidate, operationId, ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return SingleResult(providerErrorCount: 1);
        }

        if(providerResult == null)
            return SingleResult(providerErrorCount: 1);

        switch(providerResult.Status)
        {
            case PayoutOperationStatus.Pending:
                return SingleResult(pendingCount: 1);

            case PayoutOperationStatus.ResolvedTxId:
                if(string.IsNullOrWhiteSpace(providerResult.TxId))
                    return SingleResult(providerErrorCount: 1);

                // Unsafe/fake txid guard
                if(evidenceValidator.IsUnsafeEvidenceValue(providerResult.TxId))
                    return SingleResult(needsReviewCount: 1);

                var attachResult = await AttachTxIdEvidenceIfMissingAsync(candidate, providerResult.TxId,
                    checkedAt, ct);

                return attachResult switch
                {
                    TxIdAttachResult.Attached => SingleResult(attachedCount: 1,
                        attachedAttemptIds: new[] { candidate.AttemptId }),
                    TxIdAttachResult.AlreadyAttached => SingleResult(alreadyAttachedCount: 1),
                    TxIdAttachResult.NeedsReview => SingleResult(needsReviewCount: 1),
                    _ => SingleResult(providerErrorCount: 1)
                };

            case PayoutOperationStatus.ProvenNoAccept:
            case PayoutOperationStatus.Unknown:
                return SingleResult(needsReviewCount: 1);

            default:
                return SingleResult(providerErrorCount: 1);
        }
    }

    private async Task<string> GetOperationIdAsync(PayoutReconciliationAttemptSummary candidate, CancellationToken ct)
    {
        if(!string.IsNullOrWhiteSpace(candidate.ExternalOperationId))
            return candidate.ExternalOperationId;

        var confirmations = await cf.RunTx((con, tx) =>
            payoutIntentRepo.GetAttemptConfirmationsAsync(con, tx, candidate.BatchId, candidate.AttemptId,
                candidate.PoolId, ct));

        return confirmations
            .Where(x => x.Kind == PayoutExternalConfirmationKinds.OperationId)
            .Select(x => x.Value)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private bool IsCandidateEligibleForOperationIdReconciliation(PayoutReconciliationAttemptSummary candidate)
    {
        var resolution = profileResolver.Resolve(candidate.Coin);
        if(!resolution.HasProfile ||
           !evidenceValidator.IsOperationIdReconciliationEligibleByProfile(resolution.Profile))
            return false;

        var profile = resolution.Profile;

        return string.Equals(candidate.CoinFamily, profile.CoinFamily, StringComparison.Ordinal) &&
               string.Equals(candidate.Handler, profile.AdapterId, StringComparison.Ordinal) &&
               string.Equals(candidate.SendShape, profile.SendShape, StringComparison.Ordinal) &&
               string.Equals(candidate.Method, profile.SendMethod, StringComparison.Ordinal);
    }

    private async Task<TxIdAttachResult> AttachTxIdEvidenceIfMissingAsync(PayoutReconciliationAttemptSummary candidate,
        string txId, DateTime created, CancellationToken ct)
    {
        return await cf.RunTx(async (con, tx) =>
        {
            var confirmations = await payoutIntentRepo.GetAttemptConfirmationsAsync(con, tx, candidate.BatchId,
                candidate.AttemptId, candidate.PoolId, ct);

            var finalEvidencePairs = confirmations
                .Where(x => evidenceValidator.IsFinalSettlementEvidenceKind(x.Kind) &&
                            !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => new EvidencePair(x.Kind, x.Value))
                .Distinct()
                .ToArray();

            if(finalEvidencePairs.Length > 1)
                return TxIdAttachResult.NeedsReview;

            if(finalEvidencePairs.Length == 1)
                return string.Equals(finalEvidencePairs[0].Kind, PayoutExternalConfirmationKinds.TxId,
                           StringComparison.Ordinal) &&
                       string.Equals(finalEvidencePairs[0].Value, txId, StringComparison.Ordinal)
                    ? TxIdAttachResult.AlreadyAttached
                    : TxIdAttachResult.NeedsReview;

            await payoutIntentRepo.InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
            {
                PoolId = candidate.PoolId,
                Coin = candidate.Coin,
                BatchId = candidate.BatchId,
                AttemptId = candidate.AttemptId,
                Kind = PayoutExternalConfirmationKinds.TxId,
                Value = txId,
                Created = created
            }, ct);

            return TxIdAttachResult.Attached;
        });
    }

    private enum TxIdAttachResult
    {
        Attached,
        AlreadyAttached,
        NeedsReview
    }

    private readonly record struct EvidencePair(string Kind, string Value);

    private static PayoutOperationIdReconciliationResult SingleResult(int pendingCount = 0, int attachedCount = 0,
        int alreadyAttachedCount = 0, int needsReviewCount = 0, int providerErrorCount = 0,
        IReadOnlyCollection<long> attachedAttemptIds = null)
    {
        return new PayoutOperationIdReconciliationResult
        {
            CandidateCount = 1,
            ProviderPendingCount = pendingCount,
            EvidenceAttachedCount = attachedCount,
            AlreadyAttachedCount = alreadyAttachedCount,
            NeedsReviewCount = needsReviewCount,
            ProviderErrorCount = providerErrorCount,
            EvidenceAttachedAttemptIds = attachedAttemptIds ?? Array.Empty<long>()
        };
    }
}
