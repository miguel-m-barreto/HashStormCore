using System;
using System.IO;
using HashStormCore.Blockchain;
using HashStormCore.Contracts.Eventing;
using HashStormCore.DbWriter.Services;
using HashStormCore.Mappings;
using Xunit;
using BlockStatus = HashStormCore.Persistence.Model.BlockStatus;

namespace HashStormCore.Tests.Eventing;

public class ShareEventDbWriterTests
{
    [Fact]
    public void EventPipelineSchemaIncludesBlockRewardAndType()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "HashStormCore", "Persistence", "Postgres", "Scripts", "event_pipeline.sql"));

        Assert.Contains("block_reward NUMERIC NULL", sql);
        Assert.Contains("block_type TEXT NULL", sql);
    }

    [Fact]
    public void PendingBlockInsertMatchesLegacyBlockMappingDefaults()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var share = new Share
        {
            PoolId = "pool",
            BlockHeight = 123,
            NetworkDifficulty = 456.7,
            TransactionConfirmationData = "confirm-data",
            Miner = "miner",
            BlockReward = 12.34m,
            Source = "pool",
            BlockHash = "hash",
            BlockType = "pow",
            Created = created
        };
        var legacy = ManualMappings.MapBlock(share);
        legacy.Status = BlockStatus.Pending;

        var actual = ShareBatchPersistenceService.MapPendingBlock(new ShareEvent
        {
            PoolId = share.PoolId,
            BlockHeight = share.BlockHeight,
            NetworkDifficulty = share.NetworkDifficulty,
            TransactionConfirmationData = share.TransactionConfirmationData,
            Miner = share.Miner,
            BlockReward = share.BlockReward,
            Source = share.Source,
            BlockHash = share.BlockHash,
            BlockType = share.BlockType,
            Created = share.Created
        });

        Assert.Equal(legacy.PoolId, actual.PoolId);
        Assert.Equal((long)legacy.BlockHeight, actual.BlockHeight);
        Assert.Equal(legacy.NetworkDifficulty, actual.NetworkDifficulty);
        Assert.Equal(legacy.Status.ToString().ToLowerInvariant(), actual.Status);
        Assert.Equal(legacy.Type, actual.Type);
        Assert.Equal(legacy.TransactionConfirmationData, actual.TransactionConfirmationData);
        Assert.Equal(legacy.Miner, actual.Miner);
        Assert.Equal(legacy.Reward, actual.Reward);
        Assert.Equal(legacy.Effort, actual.Effort);
        Assert.Equal(legacy.MinerEffort, actual.MinerEffort);
        Assert.Equal(legacy.ConfirmationProgress, actual.ConfirmationProgress);
        Assert.Equal(legacy.Source, actual.Source);
        Assert.Equal(legacy.Hash, actual.Hash);
        Assert.Equal(legacy.Created, actual.Created);
    }
}
