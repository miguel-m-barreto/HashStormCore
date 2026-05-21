namespace HashStormCore.Persistence.Postgres.Entities;

public class PayoutIntent
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public string PoolId { get; set; }
    public string Coin { get; set; }
    public string Address { get; set; }
    public string State { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceSnapshotAmount { get; set; }
    public DateTime BalanceSnapshotUpdated { get; set; }
    public decimal PaymentThreshold { get; set; }
    public string TransactionConfirmationData { get; set; }
    public long? PaymentId { get; set; }
    public long? BalanceChangeId { get; set; }
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    public DateTime? Settled { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
}
