using Dapper;
using HashStormCore.Contracts.Eventing;
using Npgsql;

namespace HashStormCore.DbWriter.Services;

public class ShareBatchPersistenceService
{
    public ShareBatchPersistenceService(string connectionString)
    {
        this.connectionString = connectionString;
    }

    private readonly string connectionString;
    private readonly ShareEventPersistenceMapper mapper = new();

    public async Task PersistAsync(IEnumerable<ShareEvent> events, string streamName, string streamId, string consumerGroup, CancellationToken ct)
    {
        await using var con = new NpgsqlConnection(connectionString);
        await con.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);

        foreach(var shareEvent in events)
        {
            var inserted = await TryMarkProcessedAsync(con, tx, shareEvent.EventId, streamName, streamId, consumerGroup, ct);
            if(!inserted)
                continue;

            await InsertShareEventAsync(con, tx, shareEvent, ct);

            if(mapper.IsPersistibleShare(shareEvent))
                await InsertShareAsync(con, tx, mapper.Map(shareEvent), ct);

            if(shareEvent.EventType == ShareEventType.BlockCandidate)
                await InsertPendingBlockAsync(con, tx, shareEvent, ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<bool> TryMarkProcessedAsync(NpgsqlConnection con, NpgsqlTransaction tx, string eventId,
        string streamName, string streamId, string consumerGroup, CancellationToken ct)
    {
        const string query = @"INSERT INTO event_pipeline_processed_events(event_id, stream_name, stream_id, consumer_group, processed_at)
            VALUES(@eventId, @streamName, @streamId, @consumerGroup, now())
            ON CONFLICT(event_id) DO NOTHING";

        var count = await con.ExecuteAsync(new CommandDefinition(query, new
        {
            eventId,
            streamName,
            streamId,
            consumerGroup
        }, tx, cancellationToken: ct));

        return count == 1;
    }

    private static Task InsertShareEventAsync(NpgsqlConnection con, NpgsqlTransaction tx, ShareEvent shareEvent, CancellationToken ct)
    {
        const string query = @"INSERT INTO share_events(event_id, event_type, pool_id, coin_symbol, coin_family, miner, worker, source,
                created, block_height, difficulty, network_difficulty, share_multiplier, is_block_candidate, block_hash, ip_address,
                user_agent, reject_reason, error_code, error_message, transaction_confirmation_data, block_reward, block_type, inserted_at)
            VALUES(@EventId, @EventType, @PoolId, @CoinSymbol, @CoinFamily, @Miner, @Worker, @Source,
                @Created, @BlockHeight, @Difficulty, @NetworkDifficulty, @ShareMultiplier, @IsBlockCandidate, @BlockHash, @IpAddress,
                @UserAgent, @RejectReason, @ErrorCode, @ErrorMessage, @TransactionConfirmationData, @BlockReward, @BlockType, now())
            ON CONFLICT(event_id) DO NOTHING";

        return con.ExecuteAsync(new CommandDefinition(query, new
        {
            shareEvent.EventId,
            EventType = shareEvent.EventType.ToString(),
            shareEvent.PoolId,
            shareEvent.CoinSymbol,
            shareEvent.CoinFamily,
            shareEvent.Miner,
            shareEvent.Worker,
            shareEvent.Source,
            shareEvent.Created,
            BlockHeight = shareEvent.BlockHeight,
            shareEvent.Difficulty,
            shareEvent.NetworkDifficulty,
            shareEvent.ShareMultiplier,
            shareEvent.IsBlockCandidate,
            shareEvent.BlockHash,
            shareEvent.IpAddress,
            shareEvent.UserAgent,
            shareEvent.RejectReason,
            shareEvent.ErrorCode,
            shareEvent.ErrorMessage,
            shareEvent.TransactionConfirmationData,
            BlockReward = shareEvent.BlockReward,
            BlockType = string.IsNullOrWhiteSpace(shareEvent.BlockType) ? null : shareEvent.BlockType
        }, tx, cancellationToken: ct));
    }

    private static Task InsertShareAsync(NpgsqlConnection con, NpgsqlTransaction tx, PersistedShare share, CancellationToken ct)
    {
        const string query = @"INSERT INTO shares(poolid, blockheight, difficulty, networkdifficulty, miner, worker, useragent, ipaddress, source, created)
            VALUES(@PoolId, @BlockHeight, @Difficulty, @NetworkDifficulty, @Miner, @Worker, @UserAgent, @IpAddress, @Source, @Created)";

        return con.ExecuteAsync(new CommandDefinition(query, share, tx, cancellationToken: ct));
    }

    private static Task InsertPendingBlockAsync(NpgsqlConnection con, NpgsqlTransaction tx, ShareEvent shareEvent, CancellationToken ct)
    {
        const string query = @"INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type, transactionconfirmationdata,
                miner, reward, effort, minereffort, confirmationprogress, source, hash, created)
            VALUES(@poolId, @blockHeight, @networkDifficulty, @status, @type, @transactionConfirmationData,
                @miner, @reward, @effort, @minerEffort, @confirmationProgress, @source, @hash, @created)";

        return con.ExecuteAsync(new CommandDefinition(query, MapPendingBlock(shareEvent), tx, cancellationToken: ct));
    }

    public static PendingBlockInsert MapPendingBlock(ShareEvent shareEvent)
    {
        return new PendingBlockInsert
        {
            PoolId = shareEvent.PoolId,
            BlockHeight = shareEvent.BlockHeight ?? 0,
            NetworkDifficulty = shareEvent.NetworkDifficulty,
            Status = "pending",
            Type = shareEvent.BlockType,
            TransactionConfirmationData = shareEvent.TransactionConfirmationData,
            Miner = shareEvent.Miner,
            Reward = shareEvent.BlockReward,
            Effort = null,
            MinerEffort = null,
            ConfirmationProgress = 0,
            Source = shareEvent.Source,
            Hash = shareEvent.BlockHash,
            Created = shareEvent.Created
        };
    }
}

public class PendingBlockInsert
{
    public string PoolId { get; set; }
    public long BlockHeight { get; set; }
    public double NetworkDifficulty { get; set; }
    public string Status { get; set; }
    public string Type { get; set; }
    public string TransactionConfirmationData { get; set; }
    public string Miner { get; set; }
    public decimal Reward { get; set; }
    public double? Effort { get; set; }
    public double? MinerEffort { get; set; }
    public double ConfirmationProgress { get; set; }
    public string Source { get; set; }
    public string Hash { get; set; }
    public DateTime Created { get; set; }
}
