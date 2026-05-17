namespace HashStormCore.Persistence.Model.Projections;

public record PayoutBalanceProjection
{
    public string PoolId { get; init; }
    public string Address { get; init; }
    public decimal Total { get; init; }
    public decimal Reserved { get; init; }
    public decimal Ambiguous { get; init; }
    public decimal Available { get; init; }
}
