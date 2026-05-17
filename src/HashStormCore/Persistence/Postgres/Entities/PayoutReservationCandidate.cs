namespace HashStormCore.Persistence.Postgres.Entities;

public class PayoutReservationCandidate
{
    public string PoolId { get; set; }
    public string Address { get; set; }
    public decimal BalanceSnapshotAmount { get; set; }
    public DateTime BalanceSnapshotUpdated { get; set; }
    public decimal PaymentThreshold { get; set; }
    public decimal ReservedAmount { get; set; }
    public decimal AmbiguousAmount { get; set; }
    public decimal AvailableAmount { get; set; }
}
