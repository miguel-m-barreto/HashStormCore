using HashStormCore.Extensions;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class PayoutOperationIdReconciliationService
{
    public PayoutOperationIdReconciliationService(IConnectionFactory cf, IPayoutIntentRepository payoutIntentRepo)
    {
        this.cf = cf ?? throw new ArgumentNullException(nameof(cf));
        this.payoutIntentRepo = payoutIntentRepo ?? throw new ArgumentNullException(nameof(payoutIntentRepo));
    }

    private readonly IConnectionFactory cf;
    private readonly IPayoutIntentRepository payoutIntentRepo;

    public async Task<PayoutOperationIdReconciliationResult> ReconcileOperationIdsAsync(
        PayoutOperationIdReconciliationRequest request, IPayoutOperationStatusProvider provider, CancellationToken ct)
    {
        ValidateRequest(request);

        if(provider == null)
            throw new ArgumentNullException(nameof(provider));

        var candidates = await cf.RunTx((con, tx) =>
            payoutIntentRepo.GetAttemptsWithOperationIdAsync(con, tx, request.PoolId, request.Limit, ct));

        var pendingCount = 0;
        var attachedCount = 0;
        var alreadyAttachedCount = 0;
        var needsReviewCount = 0;
        var providerErrorCount = 0;
        var attachedAttemptIds = new List<long>();

        foreach(var candidate in candidates)
        {
            var operationId = await GetOperationIdAsync(candidate, ct);
            if(string.IsNullOrWhiteSpace(operationId))
            {
                needsReviewCount++;
                continue;
            }

            PayoutOperationStatusResult providerResult;

            try
            {
                providerResult = await provider.GetOperationStatusAsync(candidate, operationId, ct);
            }
            catch
            {
                providerErrorCount++;
                continue;
            }

            if(providerResult == null)
            {
                providerErrorCount++;
                continue;
            }

            switch(providerResult.Status)
            {
                case PayoutOperationStatus.Pending:
                    pendingCount++;
                    break;

                case PayoutOperationStatus.ResolvedTxId:
                    if(string.IsNullOrWhiteSpace(providerResult.TxId))
                    {
                        providerErrorCount++;
                        break;
                    }

                    var attached = await AttachTxIdEvidenceIfMissingAsync(candidate, providerResult.TxId,
                        request.CheckedAt, ct);

                    if(attached)
                    {
                        attachedCount++;
                        attachedAttemptIds.Add(candidate.AttemptId);
                    }
                    else
                        alreadyAttachedCount++;

                    break;

                case PayoutOperationStatus.ProvenNoAccept:
                case PayoutOperationStatus.Unknown:
                    needsReviewCount++;
                    break;

                default:
                    providerErrorCount++;
                    break;
            }
        }

        return new PayoutOperationIdReconciliationResult
        {
            CandidateCount = candidates.Length,
            ProviderPendingCount = pendingCount,
            EvidenceAttachedCount = attachedCount,
            AlreadyAttachedCount = alreadyAttachedCount,
            NeedsReviewCount = needsReviewCount,
            ProviderErrorCount = providerErrorCount,
            EvidenceAttachedAttemptIds = attachedAttemptIds
        };
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

    private async Task<bool> AttachTxIdEvidenceIfMissingAsync(PayoutReconciliationAttemptSummary candidate,
        string txId, DateTime created, CancellationToken ct)
    {
        return await cf.RunTx(async (con, tx) =>
        {
            var confirmations = await payoutIntentRepo.GetAttemptConfirmationsAsync(con, tx, candidate.BatchId,
                candidate.AttemptId, candidate.PoolId, ct);

            if(confirmations.Any(x => x.Kind == PayoutExternalConfirmationKinds.TxId && x.Value == txId))
                return false;

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

            return true;
        });
    }

    private static void ValidateRequest(PayoutOperationIdReconciliationRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        RequireText(request.PoolId, nameof(request.PoolId));

        if(request.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Limit), "Operation id reconciliation limit must be greater than zero");
    }

    private static void RequireText(string value, string name)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required", name);
    }
}
