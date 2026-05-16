using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace HashStormCore.ApiProvider.Services;

public class SanitizedPoolConfigService
{
    public SanitizedPoolConfigService(IConfiguration configuration)
    {
        this.configuration = configuration;
    }

    private readonly IConfiguration configuration;

    public IReadOnlyList<SanitizedPoolInfoDto> GetPools() =>
        configuration.GetSection("pools")
            .GetChildren()
            .Select(MapPool)
            .Where(x => !string.IsNullOrWhiteSpace(x.PoolId))
            .OrderBy(x => x.PoolId, StringComparer.Ordinal)
            .ToArray();

    public SanitizedPoolInfoDto GetPool(string poolId) =>
        GetPools().FirstOrDefault(x => string.Equals(x.PoolId, poolId, StringComparison.OrdinalIgnoreCase));

    private static SanitizedPoolInfoDto MapPool(IConfigurationSection pool)
    {
        var paymentProcessing = pool.GetSection("paymentProcessing");

        return new SanitizedPoolInfoDto
        {
            PoolId = pool["id"],
            Enabled = ReadBool(pool, "enabled"),
            Coin = pool["coin"],
            PoolFeePercent = SumRewardRecipientPercent(pool.GetSection("rewardRecipients")),
            PaymentProcessing = paymentProcessing.Exists()
                ? new SanitizedPoolPaymentProcessingDto
                {
                    Enabled = ReadBool(paymentProcessing, "enabled"),
                    MinimumPayment = ReadDecimal(paymentProcessing, "minimumPayment"),
                    PayoutScheme = paymentProcessing["payoutScheme"]
                }
                : null,
            Ports = MapPorts(pool.GetSection("ports"))
        };
    }

    private static IReadOnlyList<SanitizedPoolPortDto> MapPorts(IConfigurationSection ports) =>
        ports.GetChildren()
            .Select(port =>
            {
                var varDiff = port.GetSection("varDiff");

                return new SanitizedPoolPortDto
                {
                    Port = int.TryParse(port.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var portNumber)
                        ? portNumber
                        : null,
                    Name = port["name"],
                    Difficulty = ReadDouble(port, "difficulty"),
                    Tls = ReadBool(port, "tls"),
                    VarDiff = varDiff.Exists()
                        ? new SanitizedPoolVarDiffDto
                        {
                            MinDiff = ReadDouble(varDiff, "minDiff"),
                            MaxDiff = ReadDouble(varDiff, "maxDiff"),
                            TargetTime = ReadDouble(varDiff, "targetTime"),
                            RetargetTime = ReadDouble(varDiff, "retargetTime"),
                            VariancePercent = ReadDouble(varDiff, "variancePercent")
                        }
                        : null
                };
            })
            .OrderBy(x => x.Port ?? int.MaxValue)
            .ToArray();

    private static decimal SumRewardRecipientPercent(IConfigurationSection rewardRecipients)
    {
        decimal total = 0;

        foreach(var recipient in rewardRecipients.GetChildren())
        {
            var percentage = ReadDecimal(recipient, "percentage");
            if(percentage.HasValue)
                total += percentage.Value;
        }

        return total;
    }

    private static bool ReadBool(IConfiguration section, string key) =>
        bool.TryParse(section[key], out var value) && value;

    private static double? ReadDouble(IConfiguration section, string key) =>
        double.TryParse(section[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static decimal? ReadDecimal(IConfiguration section, string key) =>
        decimal.TryParse(section[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

public class SanitizedPoolInfoDto
{
    public string PoolId { get; set; }
    public bool Enabled { get; set; }
    public string Coin { get; set; }
    public decimal PoolFeePercent { get; set; }
    public SanitizedPoolPaymentProcessingDto PaymentProcessing { get; set; }
    public IReadOnlyList<SanitizedPoolPortDto> Ports { get; set; }
}

public class SanitizedPoolPaymentProcessingDto
{
    public bool Enabled { get; set; }
    public decimal? MinimumPayment { get; set; }
    public string PayoutScheme { get; set; }
}

public class SanitizedPoolPortDto
{
    public int? Port { get; set; }
    public string Name { get; set; }
    public double? Difficulty { get; set; }
    public bool Tls { get; set; }
    public SanitizedPoolVarDiffDto VarDiff { get; set; }
}

public class SanitizedPoolVarDiffDto
{
    public double? MinDiff { get; set; }
    public double? MaxDiff { get; set; }
    public double? TargetTime { get; set; }
    public double? RetargetTime { get; set; }
    public double? VariancePercent { get; set; }
}
