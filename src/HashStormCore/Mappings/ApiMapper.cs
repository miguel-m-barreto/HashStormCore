using Riok.Mapperly.Abstractions;
using ApiBalanceChange = HashStormCore.Api.Responses.BalanceChange;
using ApiBlock = HashStormCore.Api.Responses.Block;
using ApiMinerPerformanceStats = HashStormCore.Api.Responses.MinerPerformanceStats;
using ApiMinerSettings = HashStormCore.Api.Responses.MinerSettings;
using ApiPayment = HashStormCore.Api.Responses.Payment;
using ApiPoolPaymentProcessingConfig = HashStormCore.Api.Responses.ApiPoolPaymentProcessingConfig;
using ApiWorkerPerformanceStats = HashStormCore.Api.Responses.WorkerPerformanceStats;
using ApiWorkerPerformanceStatsContainer = HashStormCore.Api.Responses.WorkerPerformanceStatsContainer;
using PersistenceBalanceChange = HashStormCore.Persistence.Model.BalanceChange;
using PersistenceBlock = HashStormCore.Persistence.Model.Block;
using PersistenceMinerSettings = HashStormCore.Persistence.Model.MinerSettings;
using PersistenceMinerWorkerPerformanceStats = HashStormCore.Persistence.Model.MinerWorkerPerformanceStats;
using PersistencePayment = HashStormCore.Persistence.Model.Payment;
using PersistencePoolStats = HashStormCore.Persistence.Model.PoolStats;
using ProjectionWorkerPerformanceStats = HashStormCore.Persistence.Model.Projections.WorkerPerformanceStats;
using ProjectionWorkerPerformanceStatsContainer = HashStormCore.Persistence.Model.Projections.WorkerPerformanceStatsContainer;

namespace HashStormCore.Mappings;

[Mapper(AutoUserMappings = false)]
[UseStaticMapper(typeof(ManualMappings))]
internal partial class ApiMapper
{
    [MapperIgnoreSource(nameof(PersistenceBlock.Id))]
    [MapperIgnoreTarget(nameof(ApiBlock.InfoLink))]
    internal partial ApiBlock MapApiBlock(PersistenceBlock source);

    [MapperIgnoreSource(nameof(PersistencePayment.Id))]
    [MapperIgnoreSource(nameof(PersistencePayment.PoolId))]
    [MapperIgnoreTarget(nameof(ApiPayment.AddressInfoLink))]
    [MapperIgnoreTarget(nameof(ApiPayment.TransactionInfoLink))]
    internal partial ApiPayment MapApiPayment(PersistencePayment source);

    [MapperIgnoreSource(nameof(PersistenceBalanceChange.Id))]
    internal partial ApiBalanceChange MapApiBalanceChange(PersistenceBalanceChange source);

    [MapperIgnoreTarget(nameof(PersistenceMinerSettings.PoolId))]
    [MapperIgnoreTarget(nameof(PersistenceMinerSettings.Address))]
    [MapperIgnoreTarget(nameof(PersistenceMinerSettings.Created))]
    [MapperIgnoreTarget(nameof(PersistenceMinerSettings.Updated))]
    internal partial PersistenceMinerSettings MapMinerSettings(ApiMinerSettings source);

    [MapperIgnoreSource(nameof(PersistenceMinerSettings.PoolId))]
    [MapperIgnoreSource(nameof(PersistenceMinerSettings.Address))]
    [MapperIgnoreSource(nameof(PersistenceMinerSettings.Created))]
    [MapperIgnoreSource(nameof(PersistenceMinerSettings.Updated))]
    internal partial ApiMinerSettings MapApiMinerSettings(PersistenceMinerSettings source);

    [MapperIgnoreSource(nameof(PersistencePoolStats.Id))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.PoolId))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.LastNetworkBlockTime))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.BlockHeight))]
    [MapperIgnoreSource(nameof(PersistencePoolStats.ConnectedPeers))]
    [MapProperty(nameof(PersistencePoolStats.SharesPerSecond), nameof(HashStormCore.Api.Responses.AggregatedPoolStats.ValidSharesPerSecond))]
    internal partial HashStormCore.Api.Responses.AggregatedPoolStats MapAggregatedPoolStats(PersistencePoolStats source);

    internal partial ApiPoolPaymentProcessingConfig MapPoolPaymentProcessingConfig(HashStormCore.Configuration.PoolPaymentProcessingConfig source);

    internal partial ApiWorkerPerformanceStats MapWorkerPerformanceStats(ProjectionWorkerPerformanceStats source);
    internal partial ApiWorkerPerformanceStatsContainer MapWorkerPerformanceStatsContainer(ProjectionWorkerPerformanceStatsContainer source);

    [MapperIgnoreSource(nameof(PersistenceMinerWorkerPerformanceStats.PoolId))]
    [MapperIgnoreSource(nameof(PersistenceMinerWorkerPerformanceStats.Worker))]
    [MapperIgnoreSource(nameof(PersistenceMinerWorkerPerformanceStats.Created))]
    internal partial ApiMinerPerformanceStats MapMinerPerformanceStats(PersistenceMinerWorkerPerformanceStats source);

    [MapperIgnoreSource(nameof(HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats.Id))]
    [MapperIgnoreSource(nameof(HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats.PoolId))]
    [MapperIgnoreSource(nameof(HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats.Worker))]
    [MapperIgnoreSource(nameof(HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats.Created))]
    [MapperIgnoreSource(nameof(HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats.Partition))]
    internal partial ApiMinerPerformanceStats MapMinerPerformanceStats(HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats source);
}
