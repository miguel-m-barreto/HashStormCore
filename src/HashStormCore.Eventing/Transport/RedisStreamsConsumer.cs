using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Configuration;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace HashStormCore.Eventing.Transport;

public class RedisStreamsConsumer : IAsyncDisposable
{
    public RedisStreamsConsumer(EventPipelineBrokerConfig brokerConfig, string groupName, string consumerName)
    {
        this.brokerConfig = brokerConfig;
        this.groupName = groupName;
        this.consumerName = consumerName;
        multiplexer = ConnectionMultiplexer.Connect(brokerConfig.ConnectionString);
        db = multiplexer.GetDatabase();
    }

    private readonly EventPipelineBrokerConfig brokerConfig;
    private readonly string groupName;
    private readonly string consumerName;
    private readonly ConnectionMultiplexer multiplexer;
    private readonly IDatabase db;

    public async Task EnsureGroupAsync(string startId = "0-0")
    {
        try
        {
            await db.StreamCreateConsumerGroupAsync(brokerConfig.StreamName, groupName, startId, true);
        }
        catch(RedisServerException ex) when(ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    public async Task<IReadOnlyList<RedisShareEventBatchRecord>> ReadAsync(int count, CancellationToken ct)
    {
        return await ReadNewAsync(count, ct);
    }

    public async Task<IReadOnlyList<RedisShareEventBatchRecord>> ReadPendingFirstAsync(int count, int pendingMinIdleMs, CancellationToken ct)
    {
        var ownPending = await ReadOwnPendingAsync(count, ct);
        if(ownPending.Count > 0)
            return ownPending;

        var claimed = await AutoClaimStalePendingAsync(count, pendingMinIdleMs, ct);
        if(claimed.Count > 0)
            return claimed;

        return await ReadNewAsync(count, ct);
    }

    public async Task<IReadOnlyList<RedisShareEventBatchRecord>> ReadRangeAsync(DateTime fromUtc, DateTime toUtc, int count, CancellationToken ct)
    {
        var result = new List<RedisShareEventBatchRecord>();
        await foreach(var page in ReadRangePagesAsync(fromUtc, toUtc, count, ct))
            result.AddRange(page);

        return result;
    }

    public async IAsyncEnumerable<IReadOnlyList<RedisShareEventBatchRecord>> ReadRangePagesAsync(DateTime fromUtc, DateTime toUtc, int count,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var pageSize = Math.Max(1, count);
        RedisValue minId = $"{new DateTimeOffset(fromUtc.ToUniversalTime()).ToUnixTimeMilliseconds()}-0";
        var maxId = $"{new DateTimeOffset(toUtc.ToUniversalTime()).ToUnixTimeMilliseconds()}-9999";

        while(!ct.IsCancellationRequested)
        {
            var entries = await db.StreamRangeAsync(
                brokerConfig.StreamName,
                minId,
                maxId,
                count: pageSize,
                messageOrder: Order.Ascending);

            if(entries.Length == 0)
                yield break;

            yield return entries.Select(MapEntry).Where(x => x.Batch != null).ToArray();

            if(entries.Length < pageSize)
                yield break;

            minId = $"({entries[^1].Id}";
        }
    }

    public async Task<IReadOnlyList<RedisShareEventBatchRecord>> ReadOwnPendingAsync(int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entries = await db.StreamReadGroupAsync(
            brokerConfig.StreamName,
            groupName,
            consumerName,
            "0",
            count: count);

        return entries.Select(MapEntry).Where(x => x.Batch != null).ToArray();
    }

    public async Task<IReadOnlyList<RedisShareEventBatchRecord>> AutoClaimStalePendingAsync(int count, int pendingMinIdleMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var result = await db.StreamAutoClaimAsync(
            brokerConfig.StreamName,
            groupName,
            consumerName,
            Math.Max(1, pendingMinIdleMs),
            "0-0",
            count: count);

        return result.ClaimedEntries.Select(MapEntry).Where(x => x.Batch != null).ToArray();
    }

    private async Task<IReadOnlyList<RedisShareEventBatchRecord>> ReadNewAsync(int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entries = await db.StreamReadGroupAsync(
            brokerConfig.StreamName,
            groupName,
            consumerName,
            ">",
            count: count);

        return entries.Select(MapEntry).Where(x => x.Batch != null).ToArray();
    }

    public Task AcknowledgeAsync(params RedisValue[] messageIds)
    {
        return db.StreamAcknowledgeAsync(brokerConfig.StreamName, groupName, messageIds);
    }

    private static RedisShareEventBatchRecord MapEntry(StreamEntry entry)
    {
        var payload = entry.Values.FirstOrDefault(x => x.Name == "payload").Value;
        var batch = payload.HasValue ? JsonConvert.DeserializeObject<ShareEventBatch>(payload.ToString()) : null;

        return new RedisShareEventBatchRecord
        {
            MessageId = entry.Id,
            Batch = batch
        };
    }

    public async ValueTask DisposeAsync()
    {
        await multiplexer.CloseAsync();
        multiplexer.Dispose();
    }
}

public class RedisShareEventBatchRecord
{
    public RedisValue MessageId { get; set; }
    public ShareEventBatch Batch { get; set; }
}
