using HashStormCore.Payouts.Profiles;

namespace HashStormCore.Persistence.Model;

public enum PayoutSendAttemptPlanningStatus
{
    Created,
    AttemptsAlreadyExist,
    BatchNotFound,
    BatchNotReserved,
    NoReservedIntents
}

public record CreatePayoutSendAttemptsRequest
{
    public long BatchId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string SendShape { get; init; }
    public string Method { get; init; }
    public string AttemptPlanningPolicy { get; init; } = PayoutProfileConstants.PlanningPolicies.Default;
    public int MaxRecipientsPerAttempt { get; init; }
    public IReadOnlyCollection<ulong> IntegratedAddressPrefixes { get; init; } = Array.Empty<ulong>();
    public int AddressGroupCount { get; init; }
    public DateTime Created { get; init; }
}

public record CreatePayoutSendAttemptsResult
{
    public PayoutSendAttemptPlanningStatus Status { get; init; }
    public PayoutBatch Batch { get; init; }
    public IReadOnlyCollection<PayoutSendAttempt> Attempts { get; init; } = Array.Empty<PayoutSendAttempt>();

    public static CreatePayoutSendAttemptsResult Created(PayoutBatch batch, IReadOnlyCollection<PayoutSendAttempt> attempts)
    {
        return new CreatePayoutSendAttemptsResult
        {
            Status = PayoutSendAttemptPlanningStatus.Created,
            Batch = batch,
            Attempts = attempts
        };
    }

    public static CreatePayoutSendAttemptsResult AttemptsAlreadyExist(PayoutBatch batch)
    {
        return new CreatePayoutSendAttemptsResult
        {
            Status = PayoutSendAttemptPlanningStatus.AttemptsAlreadyExist,
            Batch = batch
        };
    }

    public static CreatePayoutSendAttemptsResult BatchNotFound()
    {
        return new CreatePayoutSendAttemptsResult
        {
            Status = PayoutSendAttemptPlanningStatus.BatchNotFound
        };
    }

    public static CreatePayoutSendAttemptsResult BatchNotReserved(PayoutBatch batch)
    {
        return new CreatePayoutSendAttemptsResult
        {
            Status = PayoutSendAttemptPlanningStatus.BatchNotReserved,
            Batch = batch
        };
    }

    public static CreatePayoutSendAttemptsResult NoReservedIntents(PayoutBatch batch)
    {
        return new CreatePayoutSendAttemptsResult
        {
            Status = PayoutSendAttemptPlanningStatus.NoReservedIntents,
            Batch = batch
        };
    }
}
