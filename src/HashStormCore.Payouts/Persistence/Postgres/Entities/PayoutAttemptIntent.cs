namespace HashStormCore.Persistence.Postgres.Entities;

public class PayoutAttemptIntent
{
    public long AttemptId { get; set; }
    public long IntentId { get; set; }
    public long BatchId { get; set; }
    public string PoolId { get; set; }
    public string Coin { get; set; }
    public string State { get; set; }
    public decimal Amount { get; set; }
    public string TransactionConfirmationData { get; set; }
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
}
