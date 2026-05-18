namespace HashStormCore.Persistence.Model;

public enum PayoutReservationStatus
{
    Created,
    ActiveBatchExists,
    NoEligibleBalances
}

public record CreatePayoutReservationRequest
{
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string CoinFamily { get; init; }
    public string Handler { get; init; }
    public string SendShape { get; init; }
    public decimal MinimumPayment { get; init; }
    public int MaxCandidates { get; init; }
    public DateTime Created { get; init; }
}

public record CreatePayoutReservationResult
{
    public PayoutReservationStatus Status { get; init; }
    public PayoutBatch Batch { get; init; }
    public IReadOnlyCollection<PayoutIntent> Intents { get; init; } = Array.Empty<PayoutIntent>();

    public static CreatePayoutReservationResult Created(PayoutBatch batch)
    {
        return new CreatePayoutReservationResult
        {
            Status = PayoutReservationStatus.Created,
            Batch = batch,
            Intents = batch?.Intents ?? Array.Empty<PayoutIntent>()
        };
    }

    public static CreatePayoutReservationResult ActiveBatchExists(PayoutBatch batch)
    {
        return new CreatePayoutReservationResult
        {
            Status = PayoutReservationStatus.ActiveBatchExists,
            Batch = batch,
            Intents = batch?.Intents ?? Array.Empty<PayoutIntent>()
        };
    }

    public static CreatePayoutReservationResult NoEligibleBalances()
    {
        return new CreatePayoutReservationResult
        {
            Status = PayoutReservationStatus.NoEligibleBalances
        };
    }
}
