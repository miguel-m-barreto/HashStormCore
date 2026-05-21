namespace HashStormCore.Persistence.Postgres.Entities;

public class PayoutBatch
{
    public long Id { get; set; }
    public string PoolId { get; set; }
    public string Coin { get; set; }
    public string CoinFamily { get; set; }
    public string Handler { get; set; }
    public string State { get; set; }
    public string SendShape { get; set; }
    public string RecipientSetHash { get; set; }
    public decimal MinimumAmount { get; set; }
    public decimal ReservedAmountSnapshot { get; set; }
    public int IntentCountSnapshot { get; set; }
    public string ExternalOperationId { get; set; }
    public string TransactionConfirmationData { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    public DateTime? Submitted { get; set; }
    public DateTime? Settled { get; set; }
    public DateTime? Reviewed { get; set; }
}
