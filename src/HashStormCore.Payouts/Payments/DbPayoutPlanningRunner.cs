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
            var skipped = 0;

            foreach(var candidate in candidates)
            {
                var resolution = payoutProfileResolver.Resolve(candidate.Coin);

                if(!resolution.HasProfile)
                {
                    skipped++;
                    continue;
                }

                var profile = resolution.Profile;
                if(!profile.ReservationReady)
                {
                    skipped++;
                    continue;
                }

                if(!string.Equals(candidate.Handler, profile.AdapterId, StringComparison.Ordinal) ||
                   !string.Equals(candidate.SendShape, profile.SendShape, StringComparison.Ordinal))
                {
                    skipped++;
                    continue;
                }

                if(string.Equals(candidate.SendShape, PayoutSendShapes.AddressGroup, StringComparison.Ordinal) &&
                   (!profile.MaxRecipientsPerAttempt.HasValue || profile.MaxRecipientsPerAttempt.Value <= 0))
                {
                    skipped++;
                    continue;
                }

                var plannerRequest = new CreatePayoutSendAttemptsRequest
                {
                    BatchId = candidate.BatchId,
                    PoolId = candidate.PoolId,
                    Coin = candidate.Coin,
                    SendShape = candidate.SendShape,
                    Method = profile.SendMethod,
                    MaxRecipientsPerAttempt = profile.MaxRecipientsPerAttempt ?? 0,
                    Created = request.Created
                };

                var result = await payoutSendAttemptPlannerService.CreateSendAttemptsAsync(con, tx, plannerRequest, ct);
                results.Add(result);
            }

            return new PayoutPlanningRunnerResult
            {
                CandidateBatchCount = candidates.Length,
                PlannedBatchCount = results.Count,
                SkippedBatchCount = skipped,
                Results = results
            };
        });
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
