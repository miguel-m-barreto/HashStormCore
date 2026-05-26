using HashStormCore.Extensions;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class DbPayoutPlanningRunner : IPayoutPlanningRunner
{
    public DbPayoutPlanningRunner(IConnectionFactory connectionFactory, IPayoutIntentRepository payoutIntentRepository,
        PayoutSendAttemptPlannerService payoutSendAttemptPlannerService, IPayoutProfileResolver payoutProfileResolver)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.payoutIntentRepository = payoutIntentRepository ??
            throw new ArgumentNullException(nameof(payoutIntentRepository));
        this.payoutSendAttemptPlannerService = payoutSendAttemptPlannerService ??
            throw new ArgumentNullException(nameof(payoutSendAttemptPlannerService));
        this.payoutProfileResolver = payoutProfileResolver ?? throw new ArgumentNullException(nameof(payoutProfileResolver));
    }

    private readonly IConnectionFactory connectionFactory;
    private readonly IPayoutIntentRepository payoutIntentRepository;
    private readonly PayoutSendAttemptPlannerService payoutSendAttemptPlannerService;
    private readonly IPayoutProfileResolver payoutProfileResolver;

    public Task<PayoutPlanningRunnerResult> CreateSendAttemptsAsync(PayoutPlanningRunnerRequest request,
        CancellationToken ct)
    {
        ValidateRequest(request);

        return connectionFactory.RunTx(async (con, tx) =>
        {
            var candidates = await payoutIntentRepository.GetReservedBatchesForPlanningAsync(con, tx, request.PoolId,
                request.MaxBatches, ct);
            var results = new List<CreatePayoutSendAttemptsResult>();
            var skipped = new List<PayoutPlanningSkippedBatch>();

            foreach(var candidate in candidates)
            {
                var resolution = payoutProfileResolver.Resolve(candidate.Coin);

                if(!resolution.HasProfile)
                {
                    skipped.Add(CreateSkippedBatch(candidate, $"profile resolution failed: {resolution.Reason}"));
                    continue;
                }

                var profile = resolution.Profile;
                if(!profile.ReservationReady)
                {
                    skipped.Add(CreateSkippedBatch(candidate,
                        string.IsNullOrWhiteSpace(profile.NotReadyReason)
                            ? "profile is not reservation-ready"
                            : profile.NotReadyReason));
                    continue;
                }

                if(!string.Equals(candidate.CoinFamily, profile.CoinFamily, StringComparison.Ordinal))
                {
                    skipped.Add(CreateSkippedBatch(candidate,
                        $"coinFamily mismatch: reserved '{candidate.CoinFamily}', resolved '{profile.CoinFamily}'"));
                    continue;
                }

                if(!string.Equals(candidate.Handler, profile.AdapterId, StringComparison.Ordinal))
                {
                    skipped.Add(CreateSkippedBatch(candidate,
                        $"handler mismatch: reserved '{candidate.Handler}', resolved '{profile.AdapterId}'"));
                    continue;
                }

                if(!string.Equals(candidate.SendShape, profile.SendShape, StringComparison.Ordinal))
                {
                    skipped.Add(CreateSkippedBatch(candidate,
                        $"sendShape mismatch: reserved '{candidate.SendShape}', resolved '{profile.SendShape}'"));
                    continue;
                }

                if(string.Equals(candidate.SendShape, PayoutSendShapes.AddressGroup, StringComparison.Ordinal) &&
                   (!profile.MaxRecipientsPerAttempt.HasValue || profile.MaxRecipientsPerAttempt.Value <= 0))
                {
                    skipped.Add(CreateSkippedBatch(candidate,
                        "address_group planning requires MaxRecipientsPerAttempt greater than zero"));
                    continue;
                }

                var plannerRequest = new CreatePayoutSendAttemptsRequest
                {
                    BatchId = candidate.BatchId,
                    PoolId = candidate.PoolId,
                    Coin = candidate.Coin,
                    SendShape = candidate.SendShape,
                    Method = profile.SendMethod,
                    AttemptPlanningPolicy = profile.AttemptPlanningPolicy,
                    MaxRecipientsPerAttempt = profile.MaxRecipientsPerAttempt ?? 0,
                    IntegratedAddressPrefixes = profile.IntegratedAddressPrefixes,
                    Created = request.Created
                };

                var result = await payoutSendAttemptPlannerService.CreateSendAttemptsAsync(con, tx, plannerRequest, ct);
                results.Add(result);
            }

            return new PayoutPlanningRunnerResult
            {
                CandidateBatchCount = candidates.Length,
                PlannedBatchCount = results.Count,
                SkippedBatchCount = skipped.Count,
                SkippedBatches = skipped,
                Results = results
            };
        });
    }

    private static PayoutPlanningSkippedBatch CreateSkippedBatch(PayoutPlanningBatchCandidate candidate, string reason)
    {
        return new PayoutPlanningSkippedBatch
        {
            BatchId = candidate.BatchId,
            PoolId = candidate.PoolId,
            Coin = candidate.Coin,
            CoinFamily = candidate.CoinFamily,
            Handler = candidate.Handler,
            SendShape = candidate.SendShape,
            Reason = reason
        };
    }

    private static void ValidateRequest(PayoutPlanningRunnerRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(string.IsNullOrWhiteSpace(request.PoolId))
            throw new ArgumentException("Pool id is required", nameof(request));

        if(request.MaxBatches <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.MaxBatches),
                "Planning batch count must be greater than zero");
    }
}
