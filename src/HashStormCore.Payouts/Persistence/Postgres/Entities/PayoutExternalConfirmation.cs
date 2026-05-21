namespace HashStormCore.Persistence.Postgres.Entities;

public class PayoutExternalConfirmation
{
    public long Id { get; set; }
    public string PoolId { get; set; }
    public string Coin { get; set; }
    public long BatchId { get; set; }
    public long? AttemptId { get; set; }
    public long? IntentId { get; set; }
    public string Kind { get; set; }
    public string Value { get; set; }
    public DateTime Created { get; set; }
}
