using Riok.Mapperly.Abstractions;
using PersistencePoolStats = HashStormCore.Persistence.Model.PoolStats;

namespace HashStormCore.Mappings;

[Mapper]
internal partial class MiningMapper
{
    [MapperIgnoreSource(nameof(PersistencePoolStats.Id))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.PoolId))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.NetworkHashrate))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.NetworkDifficulty))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.LastNetworkBlockTime))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.BlockHeight))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.ConnectedPeers))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.Created))]
    [MapperIgnoreTarget(nameof(HashStormCore.Mining.PoolStats.LastPoolBlockTime))]
    internal partial HashStormCore.Mining.PoolStats MapMiningPoolStats(PersistencePoolStats source);
}
