namespace HashStormCore.PayoutProcessor.Configuration;

public class PayoutProcessorClusterConfig
{
    public PayoutProcessorClusterPoolConfig[] Pools { get; set; } = Array.Empty<PayoutProcessorClusterPoolConfig>();
}

public class PayoutProcessorClusterPoolConfig
{
    public string Id { get; set; }
    public bool Enabled { get; set; }
    public string Coin { get; set; }
    public PayoutProcessorClusterPaymentProcessingConfig PaymentProcessing { get; set; }
    public PayoutProcessorClusterRewardRecipientConfig[] RewardRecipients { get; set; } =
        Array.Empty<PayoutProcessorClusterRewardRecipientConfig>();
}

public class PayoutProcessorClusterPaymentProcessingConfig
{
    public bool Enabled { get; set; }
    public string Engine { get; set; }
    public decimal MinimumPayment { get; set; }
}

public class PayoutProcessorClusterRewardRecipientConfig
{
    public string Address { get; set; }
    public decimal Percentage { get; set; }
    public string Type { get; set; }
    public decimal? MinimumPayment { get; set; }
}

public record PayoutProcessorPoolConfig(
    string Id,
    string Coin,
    string Engine,
    decimal MinimumPayment,
    IReadOnlyCollection<PayoutProcessorRewardRecipientConfig> RewardRecipients);

public record PayoutProcessorRewardRecipientConfig(
    string Address,
    decimal Percentage,
    string Type,
    decimal? MinimumPayment);
