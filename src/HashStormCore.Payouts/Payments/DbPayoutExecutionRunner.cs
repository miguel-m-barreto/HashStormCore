using HashStormCore.Extensions;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class DbPayoutExecutionRunner : IPayoutExecutionRunner
{
    public DbPayoutExecutionRunner(IConnectionFactory connectionFactory, IPayoutIntentRepository payoutIntentRepository,
        PayoutSendExecutorService payoutSendExecutorService, IPayoutProfileResolver payoutProfileResolver,
        IPayoutAttemptSenderRegistry senderRegistry)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.payoutIntentRepository = payoutIntentRepository ??
            throw new ArgumentNullException(nameof(payoutIntentRepository));
        this.payoutSendExecutorService = payoutSendExecutorService ??
            throw new ArgumentNullException(nameof(payoutSendExecutorService));
        this.payoutProfileResolver = payoutProfileResolver ?? throw new ArgumentNullException(nameof(payoutProfileResolver));
        this.senderRegistry = senderRegistry ?? throw new ArgumentNullException(nameof(senderRegistry));
    }

    private readonly IConnectionFactory connectionFactory;
    private readonly IPayoutIntentRepository payoutIntentRepository;
    private readonly PayoutSendExecutorService payoutSendExecutorService;
    private readonly IPayoutProfileResolver payoutProfileResolver;
    private readonly IPayoutAttemptSenderRegistry senderRegistry;

    public async Task<PayoutExecutionRunnerResult> ExecutePreparedAttemptsAsync(PayoutExecutionRunnerRequest request,
        CancellationToken ct)
    {
        ValidateRequest(request);

        var candidates = await connectionFactory.RunTx((con, tx) =>
            payoutIntentRepository.GetPreparedAttemptsForExecutionAsync(con, tx, request.PoolId,
                request.MaxAttempts, ct));

        var results = new List<PayoutSendExecutionResult>();
        var skipped = new List<PayoutExecutionSkippedAttempt>();
        var failures = new List<PayoutExecutionAttemptFailure>();

        foreach(var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var resolution = payoutProfileResolver.Resolve(candidate.Coin);
                if(!resolution.HasProfile)
                {
                    skipped.Add(CreateSkippedAttempt(candidate,
                        $"profile resolution failed: {resolution.Reason}"));
                    continue;
                }

                var profile = resolution.Profile;
                if(!profile.ReservationReady)
                {
                    skipped.Add(CreateSkippedAttempt(candidate,
                        string.IsNullOrWhiteSpace(profile.NotReadyReason)
                            ? "profile is not reservation-ready"
                            : profile.NotReadyReason));
                    continue;
                }

                if(!senderRegistry.TryGetSender(profile, out var sender))
                {
                    skipped.Add(CreateSkippedAttempt(candidate, "no registered payout sender for exact profile key"));
                    continue;
                }

                var result = await payoutSendExecutorService.ExecutePreparedAttemptAsync(new PayoutSendExecutionRequest
                {
                    PoolId = request.PoolId,
                    AttemptId = candidate.Id,
                    Started = request.Started
                }, sender, ct);

                results.Add(result);
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

        return new PayoutExecutionRunnerResult
        {
            CandidateAttemptCount = candidates.Length,
            ExecutedAttemptCount = results.Count,
            SkippedAttemptCount = skipped.Count,
            FailureCount = failures.Count,
            ExecutionResults = results,
            SkippedAttempts = skipped,
            Failures = failures
        };
    }

    private static PayoutExecutionSkippedAttempt CreateSkippedAttempt(PayoutSendAttempt attempt, string reason)
    {
        return new PayoutExecutionSkippedAttempt
        {
            AttemptId = attempt.Id,
            BatchId = attempt.BatchId,
            PoolId = attempt.PoolId,
            Coin = attempt.Coin,
            Method = attempt.Method,
            Reason = reason
        };
    }

    private static PayoutExecutionAttemptFailure CreateFailure(PayoutSendAttempt attempt, Exception ex)
    {
        return new PayoutExecutionAttemptFailure
        {
            AttemptId = attempt.Id,
            BatchId = attempt.BatchId,
            PoolId = attempt.PoolId,
            Coin = attempt.Coin,
            Method = attempt.Method,
            ErrorType = ex.GetType().Name
        };
    }

    private static void ValidateRequest(PayoutExecutionRunnerRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(string.IsNullOrWhiteSpace(request.PoolId))
            throw new ArgumentException("Pool id is required", nameof(request));

        if(request.MaxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.MaxAttempts),
                "Execution attempt count must be greater than zero");
    }
}
