using HashStormCore.Extensions;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class DbPayoutSettlementRunner : IPayoutSettlementRunner
{
    public DbPayoutSettlementRunner(IConnectionFactory connectionFactory,
        IPayoutSettlementRepository payoutSettlementRepository,
        PayoutSettlementService payoutSettlementService)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.payoutSettlementRepository = payoutSettlementRepository ??
            throw new ArgumentNullException(nameof(payoutSettlementRepository));
        this.payoutSettlementService = payoutSettlementService ??
            throw new ArgumentNullException(nameof(payoutSettlementService));
    }

    private readonly IConnectionFactory connectionFactory;
    private readonly IPayoutSettlementRepository payoutSettlementRepository;
    private readonly PayoutSettlementService payoutSettlementService;

    public async Task<PayoutSettlementRunnerResult> SettleAcceptedAttemptsAsync(PayoutSettlementRunnerRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateRequest(request);

        var candidates = await connectionFactory.RunTx((con, tx) =>
            payoutSettlementRepository.GetAcceptedAttemptsForSettlementAsync(con, tx, request.PoolId,
                request.Limit, ct));
        var results = new List<PayoutSettlementResult>();
        var skipped = new List<PayoutSettlementSkippedCandidate>();
        var failures = new List<PayoutSettlementAttemptFailure>();

        foreach(var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var eligibility = payoutSettlementService.ValidateSettlementCandidate(candidate);
                if(!eligibility.IsEligible)
                {
                    skipped.Add(CreateSkipped(candidate, eligibility.Status.ToString(), eligibility.Reason));
                    continue;
                }

                var result = await connectionFactory.RunTx((con, tx) =>
                    payoutSettlementService.SettleEligibleCandidateAsync(con, tx, candidate, request.SettledAt, ct));
                results.Add(result);

                if(result.Status != PayoutSettlementStatus.Settled &&
                   result.Status != PayoutSettlementStatus.AlreadySettled)
                {
                    skipped.Add(CreateSkipped(candidate, result.Status.ToString(),
                        $"Settlement service returned {result.Status}"));
                }
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

        return new PayoutSettlementRunnerResult
        {
            CandidateCount = candidates.Length,
            SettledCount = results.Count(x => x.Status == PayoutSettlementStatus.Settled),
            AlreadySettledCount = results.Count(x => x.Status == PayoutSettlementStatus.AlreadySettled),
            SkippedCount = skipped.Count,
            FailureCount = failures.Count,
            SettlementResults = results,
            SkippedCandidates = skipped,
            Failures = failures
        };
    }

    private static PayoutSettlementSkippedCandidate CreateSkipped(PayoutSettlementAttemptCandidate candidate,
        string status, string reason)
    {
        return new PayoutSettlementSkippedCandidate
        {
            BatchId = candidate.BatchId,
            AttemptId = candidate.AttemptId,
            PoolId = candidate.PoolId,
            Coin = candidate.Coin,
            Method = candidate.Method,
            EvidenceKind = candidate.EvidenceKind,
            Status = status,
            Reason = reason
        };
    }

    private static PayoutSettlementAttemptFailure CreateFailure(PayoutSettlementAttemptCandidate candidate,
        Exception ex)
    {
        return new PayoutSettlementAttemptFailure
        {
            BatchId = candidate.BatchId,
            AttemptId = candidate.AttemptId,
            PoolId = candidate.PoolId,
            Coin = candidate.Coin,
            Method = candidate.Method,
            EvidenceKind = candidate.EvidenceKind,
            ErrorType = ex.GetType().Name
        };
    }

    private static void ValidateRequest(PayoutSettlementRunnerRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(string.IsNullOrWhiteSpace(request.PoolId))
            throw new ArgumentException("Pool id is required", nameof(request));

        if(request.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Limit),
                "Settlement candidate limit must be greater than zero");

        if(request.SettledAt == default)
            throw new ArgumentException("SettledAt must be set", nameof(request));
    }
}
