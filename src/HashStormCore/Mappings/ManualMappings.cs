using System.Text.Json;
using Newtonsoft.Json.Linq;
using Riok.Mapperly.Abstractions;
using ApiCoinConfig = HashStormCore.Api.Responses.ApiCoinConfig;
using ApiMinerStats = HashStormCore.Api.Responses.MinerStats;
using ApiPoolInfo = HashStormCore.Api.Responses.PoolInfo;
using ApiPoolPayoutSchemeConfig = HashStormCore.Api.Responses.ApiPoolPayoutSchemeConfig;
using BlockchainShare = HashStormCore.Blockchain.Share;
using PersistenceBlock = HashStormCore.Persistence.Model.Block;
using PersistencePoolStats = HashStormCore.Persistence.Model.PoolStats;
using PersistenceShare = HashStormCore.Persistence.Model.Share;

namespace HashStormCore.Mappings;

internal static class ManualMappings
{
    private static readonly JsonSerializerOptions jsonSerializerOptions = new(JsonSerializerDefaults.Web);

    [UserMapping]
    internal static PersistenceShare MapShare(BlockchainShare source)
    {
        if(source == null)
            return null;

        return new PersistenceShare
        {
            PoolId = source.PoolId,
            BlockHeight = (ulong) source.BlockHeight,
            Miner = source.Miner,
            Worker = source.Worker,
            UserAgent = source.UserAgent,
            Difficulty = source.Difficulty,
            NetworkDifficulty = source.NetworkDifficulty,
            IpAddress = source.IpAddress,
            Source = source.Source,
            Created = source.Created
        };
    }

    [UserMapping]
    internal static HashStormCore.Persistence.Postgres.Entities.Share MapShareEntity(PersistenceShare source)
    {
        if(source == null)
            return null;

        return new HashStormCore.Persistence.Postgres.Entities.Share
        {
            PoolId = source.PoolId,
            BlockHeight = (long) source.BlockHeight,
            Miner = source.Miner,
            Worker = source.Worker,
            UserAgent = source.UserAgent,
            Difficulty = source.Difficulty,
            NetworkDifficulty = source.NetworkDifficulty,
            IpAddress = source.IpAddress,
            Source = source.Source,
            Created = source.Created
        };
    }

    [UserMapping]
    internal static PersistenceShare MapShare(HashStormCore.Persistence.Postgres.Entities.Share source)
    {
        if(source == null)
            return null;

        return new PersistenceShare
        {
            PoolId = source.PoolId,
            BlockHeight = (ulong) source.BlockHeight,
            Miner = source.Miner,
            Worker = source.Worker,
            UserAgent = source.UserAgent,
            Difficulty = source.Difficulty,
            NetworkDifficulty = source.NetworkDifficulty,
            IpAddress = source.IpAddress,
            Source = source.Source,
            Created = source.Created
        };
    }

    [UserMapping]
    internal static PersistenceBlock MapBlock(BlockchainShare source)
    {
        if(source == null)
            return null;

        return new PersistenceBlock
        {
            PoolId = source.PoolId,
            BlockHeight = (ulong) source.BlockHeight,
            NetworkDifficulty = source.NetworkDifficulty,
            TransactionConfirmationData = source.TransactionConfirmationData,
            Miner = source.Miner,
            Reward = source.BlockReward,
            Source = source.Source,
            Hash = source.BlockHash,
            Type = source.BlockType,
            Created = source.Created
        };
    }

    [UserMapping]
    internal static string MapBlockStatus(HashStormCore.Persistence.Model.BlockStatus source)
    {
        return source.ToString().ToLowerInvariant();
    }

    [UserMapping]
    internal static HashStormCore.Persistence.Model.BlockStatus MapBlockStatus(string source)
    {
        if(string.IsNullOrEmpty(source))
            return default;

        return Enum.Parse<HashStormCore.Persistence.Model.BlockStatus>(source, true);
    }

    [UserMapping]
    internal static JToken MapJToken(JToken source)
    {
        return source;
    }

    [UserMapping]
    internal static ApiCoinConfig MapCoinConfig(HashStormCore.Configuration.CoinTemplate source)
    {
        if(source == null)
            return null;

        return new ApiCoinConfig
        {
            Type = source.Symbol,
            Name = source.Name,
            Symbol = source.Symbol,
            Website = source.Website,
            Market = source.Market,
            Family = source.Family.ToString().ToLowerInvariant(),
            Algorithm = source.GetAlgorithmName(),
            Twitter = source.Twitter,
            Discord = source.Discord,
            Telegram = source.Telegram,
            Github = source.Github,
            CanonicalName = source.CanonicalName
        };
    }

    internal static ApiPoolInfo MapPoolInfo(HashStormCore.Configuration.PoolConfig source)
    {
        if(source == null)
            return null;

        return new ApiPoolInfo
        {
            Id = source.Id,
            Ports = source.Ports,
            ClientConnectionTimeout = source.ClientConnectionTimeout,
            JobRebroadcastTimeout = source.JobRebroadcastTimeout,
            BlockRefreshInterval = source.BlockRefreshInterval,
            Address = source.Address
        };
    }

    internal static PersistencePoolStats ApplyPoolStats(HashStormCore.Mining.PoolStats source, PersistencePoolStats target)
    {
        if(target == null)
            return null;

        if(source == null)
            return target;

        return target with
        {
            ConnectedMiners = source.ConnectedMiners,
            PoolHashrate = source.PoolHashrate,
            SharesPerSecond = source.SharesPerSecond
        };
    }

    internal static PersistencePoolStats ApplyBlockchainStats(HashStormCore.Blockchain.BlockchainStats source, PersistencePoolStats target)
    {
        if(target == null)
            return null;

        if(source == null)
            return target;

        return target with
        {
            NetworkHashrate = source.NetworkHashrate,
            NetworkDifficulty = source.NetworkDifficulty,
            LastNetworkBlockTime = source.LastNetworkBlockTime,
            BlockHeight = (long) source.BlockHeight,
            ConnectedPeers = source.ConnectedPeers
        };
    }

    internal static HashStormCore.Blockchain.BlockchainStats MapBlockchainStats(PersistencePoolStats source)
    {
        if(source == null)
            return null;

        return new HashStormCore.Blockchain.BlockchainStats
        {
            NetworkHashrate = source.NetworkHashrate,
            NetworkDifficulty = source.NetworkDifficulty,
            LastNetworkBlockTime = source.LastNetworkBlockTime,
            BlockHeight = (ulong) source.BlockHeight,
            ConnectedPeers = source.ConnectedPeers
        };
    }

    internal static ApiMinerStats MapMinerStats(HashStormCore.Persistence.Model.Projections.MinerStats source)
    {
        if(source == null)
            return null;

        return new ApiMinerStats
        {
            PendingShares = source.PendingShares,
            PendingBalance = source.PendingBalance,
            TotalPaid = source.TotalPaid,
            TodayPaid = source.TodayPaid,
            MinerEffort = source.MinerEffort,
            TotalConfirmedBlocks = source.TotalConfirmedBlocks,
            TotalPendingBlocks = source.TotalPendingBlocks
        };
    }

    [UserMapping]
    internal static ApiPoolPayoutSchemeConfig MapPoolPayoutSchemeConfig(JToken source)
    {
        return source?.ToObject<ApiPoolPayoutSchemeConfig>() ?? new ApiPoolPayoutSchemeConfig();
    }

    [UserMapping]
    internal static Dictionary<string, JsonElement> MapJsonElementDictionary(IDictionary<string, object> source)
    {
        if(source == null)
            return new Dictionary<string, JsonElement>();

        return source.ToDictionary(x => x.Key, x => SerializeToElement(x.Value));
    }

    private static JsonElement SerializeToElement(object value)
    {
        if(value == null)
            return JsonSerializer.SerializeToElement<object>(null, jsonSerializerOptions);

        return JsonSerializer.SerializeToElement(value, value.GetType(), jsonSerializerOptions);
    }
}
