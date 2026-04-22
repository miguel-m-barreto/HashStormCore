using Newtonsoft.Json.Linq;
using ApiBalanceChange = HashStormCore.Api.Responses.BalanceChange;
using ApiBlock = HashStormCore.Api.Responses.Block;
using ApiMinerPerformanceStats = HashStormCore.Api.Responses.MinerPerformanceStats;
using ApiMinerSettings = HashStormCore.Api.Responses.MinerSettings;
using ApiMinerStats = HashStormCore.Api.Responses.MinerStats;
using ApiPayment = HashStormCore.Api.Responses.Payment;
using ApiPoolInfo = HashStormCore.Api.Responses.PoolInfo;
using ApiWorkerPerformanceStatsContainer = HashStormCore.Api.Responses.WorkerPerformanceStatsContainer;
using BlockchainShare = HashStormCore.Blockchain.Share;
using PersistenceBalance = HashStormCore.Persistence.Model.Balance;
using PersistenceBalanceChange = HashStormCore.Persistence.Model.BalanceChange;
using PersistenceBlock = HashStormCore.Persistence.Model.Block;
using PersistenceMinerSettings = HashStormCore.Persistence.Model.MinerSettings;
using PersistenceMinerWorkerPerformanceStats = HashStormCore.Persistence.Model.MinerWorkerPerformanceStats;
using PersistencePayment = HashStormCore.Persistence.Model.Payment;
using PersistencePoolStats = HashStormCore.Persistence.Model.PoolStats;
using PersistenceShare = HashStormCore.Persistence.Model.Share;
using ProjectionMinerStats = HashStormCore.Persistence.Model.Projections.MinerStats;
using ProjectionWorkerPerformanceStatsContainer = HashStormCore.Persistence.Model.Projections.WorkerPerformanceStatsContainer;

namespace HashStormCore.Mappings;

public interface IObjectMapper
{
    PersistenceShare MapShare(BlockchainShare source);
    PersistenceShare MapShare(HashStormCore.Persistence.Postgres.Entities.Share source);
    HashStormCore.Persistence.Postgres.Entities.Share MapShareEntity(PersistenceShare source);

    PersistenceBlock MapBlock(BlockchainShare source);
    PersistenceBlock MapBlock(HashStormCore.Persistence.Postgres.Entities.Block source);
    HashStormCore.Persistence.Postgres.Entities.Block MapBlockEntity(PersistenceBlock source);
    ApiBlock MapApiBlock(PersistenceBlock source);

    string MapBlockStatus(HashStormCore.Persistence.Model.BlockStatus source);
    JToken MapJToken(JToken source);

    HashStormCore.Api.Responses.ApiCoinConfig MapCoinConfig(HashStormCore.Configuration.CoinTemplate source);
    ApiPoolInfo MapPoolInfo(HashStormCore.Configuration.PoolConfig source);

    PersistencePoolStats ApplyPoolStats(HashStormCore.Mining.PoolStats source, PersistencePoolStats target);
    PersistencePoolStats ApplyBlockchainStats(HashStormCore.Blockchain.BlockchainStats source, PersistencePoolStats target);
    PersistencePoolStats MapPoolStats(HashStormCore.Persistence.Postgres.Entities.PoolStats source);
    HashStormCore.Persistence.Postgres.Entities.PoolStats MapPoolStatsEntity(PersistencePoolStats source);
    HashStormCore.Mining.PoolStats MapMiningPoolStats(PersistencePoolStats source);
    HashStormCore.Blockchain.BlockchainStats MapBlockchainStats(PersistencePoolStats source);
    HashStormCore.Api.Responses.AggregatedPoolStats MapAggregatedPoolStats(PersistencePoolStats source);

    PersistenceBalance MapBalance(HashStormCore.Persistence.Postgres.Entities.Balance source);
    HashStormCore.Persistence.Postgres.Entities.Balance MapBalanceEntity(PersistenceBalance source);

    PersistencePayment MapPayment(HashStormCore.Persistence.Postgres.Entities.Payment source);
    HashStormCore.Persistence.Postgres.Entities.Payment MapPaymentEntity(PersistencePayment source);
    ApiPayment MapApiPayment(PersistencePayment source);

    PersistenceBalanceChange MapBalanceChange(HashStormCore.Persistence.Postgres.Entities.BalanceChange source);
    ApiBalanceChange MapApiBalanceChange(PersistenceBalanceChange source);

    PersistenceMinerSettings MapMinerSettings(ApiMinerSettings source);
    PersistenceMinerSettings MapMinerSettings(HashStormCore.Persistence.Postgres.Entities.MinerSettings source);
    HashStormCore.Persistence.Postgres.Entities.MinerSettings MapMinerSettingsEntity(PersistenceMinerSettings source);
    ApiMinerSettings MapApiMinerSettings(PersistenceMinerSettings source);

    PersistenceMinerWorkerPerformanceStats MapMinerWorkerPerformanceStats(HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats source);
    HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats MapMinerWorkerPerformanceStatsEntity(PersistenceMinerWorkerPerformanceStats source);
    ApiMinerPerformanceStats MapMinerPerformanceStats(PersistenceMinerWorkerPerformanceStats source);
    ApiMinerPerformanceStats MapMinerPerformanceStats(HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats source);

    ApiMinerStats MapMinerStats(ProjectionMinerStats source);
    ApiWorkerPerformanceStatsContainer MapWorkerPerformanceStatsContainer(ProjectionWorkerPerformanceStatsContainer source);
    ApiWorkerPerformanceStatsContainer[] MapWorkerPerformanceStatsContainers(ProjectionWorkerPerformanceStatsContainer[] source);
}
