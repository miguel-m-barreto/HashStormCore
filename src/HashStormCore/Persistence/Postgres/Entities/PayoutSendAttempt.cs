namespace HashStormCore.Persistence.Postgres.Entities;

public class PayoutSendAttempt
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public string PoolId { get; set; }
    public string Coin { get; set; }
    public int AttemptNo { get; set; }
    public string Method { get; set; }
    public string State { get; set; }
    public string RequestHash { get; set; }
    public string RequestSummary { get; set; }
    public int RecipientCount { get; set; }
    public decimal AmountSnapshot { get; set; }
    public string ExternalOperationId { get; set; }
    public string TransactionConfirmationData { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    public DateTime? Completed { get; set; }
}
