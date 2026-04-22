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

internal sealed class ObjectMapper : IObjectMapper
{
    private readonly ApiMapper apiMapper = new();
    private readonly PersistenceMapper persistenceMapper = new();
    private readonly MiningMapper miningMapper = new();

    public PersistenceShare MapShare(BlockchainShare source)
    {
        return ManualMappings.MapShare(source);
    }

    public PersistenceShare MapShare(HashStormCore.Persistence.Postgres.Entities.Share source)
    {
        return ManualMappings.MapShare(source);
    }

    public HashStormCore.Persistence.Postgres.Entities.Share MapShareEntity(PersistenceShare source)
    {
        return ManualMappings.MapShareEntity(source);
    }

    public PersistenceBlock MapBlock(BlockchainShare source)
    {
        return ManualMappings.MapBlock(source);
    }

    public PersistenceBlock MapBlock(HashStormCore.Persistence.Postgres.Entities.Block source)
    {
        return persistenceMapper.MapBlock(source);
    }

    public HashStormCore.Persistence.Postgres.Entities.Block MapBlockEntity(PersistenceBlock source)
    {
        return persistenceMapper.MapBlockEntity(source);
    }

    public ApiBlock MapApiBlock(PersistenceBlock source)
    {
        return apiMapper.MapApiBlock(source);
    }

    public string MapBlockStatus(HashStormCore.Persistence.Model.BlockStatus source)
    {
        return ManualMappings.MapBlockStatus(source);
    }

    public JToken MapJToken(JToken source)
    {
        return ManualMappings.MapJToken(source);
    }

    public HashStormCore.Api.Responses.ApiCoinConfig MapCoinConfig(HashStormCore.Configuration.CoinTemplate source)
    {
        return ManualMappings.MapCoinConfig(source);
    }

    public ApiPoolInfo MapPoolInfo(HashStormCore.Configuration.PoolConfig source)
    {
        var target = ManualMappings.MapPoolInfo(source);

        if(target == null)
            return null;

        target.Coin = MapCoinConfig(source.Template);
        target.PaymentProcessing = source.PaymentProcessing == null ? null : apiMapper.MapPoolPaymentProcessingConfig(source.PaymentProcessing);

        return target;
    }

    public PersistencePoolStats ApplyPoolStats(HashStormCore.Mining.PoolStats source, PersistencePoolStats target)
    {
        return ManualMappings.ApplyPoolStats(source, target);
    }

    public PersistencePoolStats ApplyBlockchainStats(HashStormCore.Blockchain.BlockchainStats source, PersistencePoolStats target)
    {
        return ManualMappings.ApplyBlockchainStats(source, target);
    }

    public PersistencePoolStats MapPoolStats(HashStormCore.Persistence.Postgres.Entities.PoolStats source)
    {
        return persistenceMapper.MapPoolStats(source);
    }

    public HashStormCore.Persistence.Postgres.Entities.PoolStats MapPoolStatsEntity(PersistencePoolStats source)
    {
        return persistenceMapper.MapPoolStatsEntity(source);
    }

    public HashStormCore.Mining.PoolStats MapMiningPoolStats(PersistencePoolStats source)
    {
        return miningMapper.MapMiningPoolStats(source);
    }

    public HashStormCore.Blockchain.BlockchainStats MapBlockchainStats(PersistencePoolStats source)
    {
        return ManualMappings.MapBlockchainStats(source);
    }

    public HashStormCore.Api.Responses.AggregatedPoolStats MapAggregatedPoolStats(PersistencePoolStats source)
    {
        return apiMapper.MapAggregatedPoolStats(source);
    }

    public PersistenceBalance MapBalance(HashStormCore.Persistence.Postgres.Entities.Balance source)
    {
        return persistenceMapper.MapBalance(source);
    }

    public HashStormCore.Persistence.Postgres.Entities.Balance MapBalanceEntity(PersistenceBalance source)
    {
        return persistenceMapper.MapBalanceEntity(source);
    }

    public PersistencePayment MapPayment(HashStormCore.Persistence.Postgres.Entities.Payment source)
    {
        return persistenceMapper.MapPayment(source);
    }

    public HashStormCore.Persistence.Postgres.Entities.Payment MapPaymentEntity(PersistencePayment source)
    {
        return persistenceMapper.MapPaymentEntity(source);
    }

    public ApiPayment MapApiPayment(PersistencePayment source)
    {
        return apiMapper.MapApiPayment(source);
    }

    public PersistenceBalanceChange MapBalanceChange(HashStormCore.Persistence.Postgres.Entities.BalanceChange source)
    {
        return persistenceMapper.MapBalanceChange(source);
    }

    public ApiBalanceChange MapApiBalanceChange(PersistenceBalanceChange source)
    {
        return apiMapper.MapApiBalanceChange(source);
    }

    public PersistenceMinerSettings MapMinerSettings(ApiMinerSettings source)
    {
        return apiMapper.MapMinerSettings(source);
    }

    public PersistenceMinerSettings MapMinerSettings(HashStormCore.Persistence.Postgres.Entities.MinerSettings source)
    {
        return persistenceMapper.MapMinerSettings(source);
    }

    public HashStormCore.Persistence.Postgres.Entities.MinerSettings MapMinerSettingsEntity(PersistenceMinerSettings source)
    {
        return persistenceMapper.MapMinerSettingsEntity(source);
    }

    public ApiMinerSettings MapApiMinerSettings(PersistenceMinerSettings source)
    {
        return apiMapper.MapApiMinerSettings(source);
    }

    public PersistenceMinerWorkerPerformanceStats MapMinerWorkerPerformanceStats(HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats source)
    {
        return persistenceMapper.MapMinerWorkerPerformanceStats(source);
    }

    public HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats MapMinerWorkerPerformanceStatsEntity(PersistenceMinerWorkerPerformanceStats source)
    {
        return persistenceMapper.MapMinerWorkerPerformanceStatsEntity(source);
    }

    public ApiMinerPerformanceStats MapMinerPerformanceStats(PersistenceMinerWorkerPerformanceStats source)
    {
        return apiMapper.MapMinerPerformanceStats(source);
    }

    public ApiMinerPerformanceStats MapMinerPerformanceStats(HashStormCore.Persistence.Postgres.Entities.MinerWorkerPerformanceStats source)
    {
        return apiMapper.MapMinerPerformanceStats(source);
    }

    public ApiMinerStats MapMinerStats(ProjectionMinerStats source)
    {
        var target = ManualMappings.MapMinerStats(source);

        if(target == null)
            return null;

        target.Performance = source.Performance == null ? null : apiMapper.MapWorkerPerformanceStatsContainer(source.Performance);

        return target;
    }

    public ApiWorkerPerformanceStatsContainer MapWorkerPerformanceStatsContainer(ProjectionWorkerPerformanceStatsContainer source)
    {
        return apiMapper.MapWorkerPerformanceStatsContainer(source);
    }

    public ApiWorkerPerformanceStatsContainer[] MapWorkerPerformanceStatsContainers(ProjectionWorkerPerformanceStatsContainer[] source)
    {
        if(source == null)
            return null;

        return source.Select(apiMapper.MapWorkerPerformanceStatsContainer).ToArray();
    }
}
