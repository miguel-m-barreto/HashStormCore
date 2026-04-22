using Riok.Mapperly.Abstractions;
using PgBalance = HashStormCore.Persistence.Postgres.Entities.Balance;
using PgBlock = HashStormCore.Persistence.Postgres.Entities.Block;
using PgMinerSettings = HashStormCore.Persistence.Postgres.Entities.MinerSettings;
using PgMinerWorkerPerformanceStats = HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats;
using PgPayment = HashStormCore.Persistence.Postgres.Entities.Payment;
using PgPoolStats = HashStormCore.Persistence.Postgres.Entities.PoolStats;
using PersistenceBalance = HashStormCore.Persistence.Model.Balance;
using PersistenceBalanceChange = HashStormCore.Persistence.Model.BalanceChange;
using PersistenceBlock = HashStormCore.Persistence.Model.Block;
using PersistenceMinerSettings = HashStormCore.Persistence.Model.MinerSettings;
using PersistenceMinerWorkerPerformanceStats = HashStormCore.Persistence.Model.MinerWorkerPerformanceStats;
using PersistencePayment = HashStormCore.Persistence.Model.Payment;
using PersistencePoolStats = HashStormCore.Persistence.Model.PoolStats;

namespace HashStormCore.Mappings;

[Mapper(AutoUserMappings = false)]
[UseStaticMapper(typeof(ManualMappings))]
internal partial class PersistenceMapper
{
    internal partial PgBlock MapBlockEntity(PersistenceBlock source);
    internal partial PersistenceBlock MapBlock(PgBlock source);

    internal partial PgBalance MapBalanceEntity(PersistenceBalance source);
    internal partial PersistenceBalance MapBalance(PgBalance source);

    [MapperIgnoreSource(nameof(PersistencePayment.Id))]
    [MapperIgnoreTarget(nameof(PgPayment.Id))]
    internal partial PgPayment MapPaymentEntity(PersistencePayment source);
    internal partial PersistencePayment MapPayment(PgPayment source);

    [MapperIgnoreSource(nameof(HashStormCore.Persistence.Postgres.Entities.BalanceChange.Tags))]
    internal partial PersistenceBalanceChange MapBalanceChange(HashStormCore.Persistence.Postgres.Entities.BalanceChange source);

    internal partial PgPoolStats MapPoolStatsEntity(PersistencePoolStats source);
    internal partial PersistencePoolStats MapPoolStats(PgPoolStats source);

    internal partial PgMinerSettings MapMinerSettingsEntity(PersistenceMinerSettings source);
    internal partial PersistenceMinerSettings MapMinerSettings(PgMinerSettings source);

    [MapperIgnoreTarget(nameof(PgMinerWorkerPerformanceStats.Id))]
    [MapperIgnoreTarget(nameof(PgMinerWorkerPerformanceStats.Partition))]
    internal partial PgMinerWorkerPerformanceStats MapMinerWorkerPerformanceStatsEntity(PersistenceMinerWorkerPerformanceStats source);

    [MapperIgnoreSource(nameof(PgMinerWorkerPerformanceStats.Id))]
    [MapperIgnoreSource(nameof(PgMinerWorkerPerformanceStats.Partition))]
    internal partial PersistenceMinerWorkerPerformanceStats MapMinerWorkerPerformanceStats(PgMinerWorkerPerformanceStats source);
}
