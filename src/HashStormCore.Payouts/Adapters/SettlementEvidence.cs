namespace HashStormCore.Payouts.Adapters;

public record SettlementEvidence
{
    public string Kind { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}
