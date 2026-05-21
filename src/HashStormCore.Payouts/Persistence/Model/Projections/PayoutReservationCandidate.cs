namespace HashStormCore.Persistence.Model.Projections;

public record PayoutReservationCandidate
{
    public string PoolId { get; init; }
    public string Address { get; init; }
    public decimal BalanceSnapshotAmount { get; init; }
    public DateTime BalanceSnapshotUpdated { get; init; }
    public decimal PaymentThreshold { get; init; }
    public decimal ReservedAmount { get; init; }
    public decimal AmbiguousAmount { get; init; }
    public decimal AvailableAmount { get; init; }
}
