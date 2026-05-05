using HashStormCore.Eventing.Configuration;
using HashStormCore.Eventing.Transport;
using HashStormCore.LiveAggregator.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace HashStormCore.LiveAggregator.Services;

public class LiveAggregatorService : BackgroundService
{
    public LiveAggregatorService(LiveAggregatorConfig config, ILogger<LiveAggregatorService> logger)
    {
        this.config = config;
        this.logger = logger;
    }

    private readonly LiveAggregatorConfig config;
    private readonly ILogger<LiveAggregatorService> logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var broker = new EventPipelineBrokerConfig
        {
            ConnectionString = config.RedisConnectionString,
            StreamName = config.StreamName
        };

        await using var consumer = new RedisStreamsConsumer(broker, "live-aggregator", config.ConsumerName);
        await using var writer = new RedisLiveStateWriter(config.RedisConnectionString, config.RedisTtlSeconds, config.LiveWindowSeconds, config.BucketSeconds);

        await consumer.EnsureGroupAsync("$");

        foreach(var pool in config.Pools)
            await writer.ClearPoolStateAsync(pool);

        foreach(var pool in config.Pools)
            await writer.MarkStatusAsync(pool, "warming_up", config.LiveWindowSeconds, 0);

        await RebuildFromRetainedStreamAsync(consumer, writer, stoppingToken);

        while(!stoppingToken.IsCancellationRequested)
        {
            var records = await consumer.ReadPendingFirstAsync(config.ReadBatchSize, config.PendingMinIdleMs, stoppingToken);
            if(records.Count == 0)
            {
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            foreach(var record in records)
            {
                if(record.Batch == null)
                    continue;

                await writer.WriteBatchAsync(record.Batch.Events, stoppingToken);
                await consumer.AcknowledgeAsync(record.MessageId);
            }
        }
    }

    private async Task RebuildFromRetainedStreamAsync(RedisStreamsConsumer consumer, RedisLiveStateWriter writer, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var from = now.AddSeconds(-config.LiveWindowSeconds);
        await foreach(var records in consumer.ReadRangePagesAsync(from, now, Math.Max(config.ReadBatchSize, 1000), ct))
        {
            var events = records
                .Where(x => x.Batch != null)
                .SelectMany(x => x.Batch.Events)
                .Where(x => x.Created.ToUniversalTime() >= from && x.Created.ToUniversalTime() <= now)
                .ToArray();

            if(events.Length > 0)
                await writer.WriteBatchAsync(events, ct);
        }
    }
}
