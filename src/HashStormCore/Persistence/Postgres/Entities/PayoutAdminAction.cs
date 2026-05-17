namespace HashStormCore.Persistence.Postgres.Entities;

public class PayoutAdminAction
{
    public long Id { get; set; }
    public string PoolId { get; set; }
    public long? BatchId { get; set; }
    public long? IntentId { get; set; }
    public long? AttemptId { get; set; }
    public string Action { get; set; }
    public string Reason { get; set; }
    public string Operator { get; set; }
    public string Metadata { get; set; }
    public DateTime Created { get; set; }
}
