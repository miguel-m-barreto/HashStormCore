using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Abstractions;
using HashStormCore.Eventing.Configuration;
using HashStormCore.Eventing.Publishing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace HashStormCore.Eventing.Transport;

public class RedisStreamsShareEventBatchTransport : IShareEventBatchTransport, IAsyncDisposable
{
    public RedisStreamsShareEventBatchTransport(EventPipelineBrokerConfig brokerConfig, EventPipelineRetentionConfig retentionConfig,
        ILogger<RedisStreamsShareEventBatchTransport> logger = null)
    {
        this.brokerConfig = brokerConfig;
        this.retentionConfig = retentionConfig ?? new EventPipelineRetentionConfig();
        this.logger = logger ?? NullLogger<RedisStreamsShareEventBatchTransport>.Instance;

        if(brokerConfig.StartupRequired)
            Connect();
    }

    private readonly EventPipelineBrokerConfig brokerConfig;
    private readonly EventPipelineRetentionConfig retentionConfig;
    private readonly ILogger<RedisStreamsShareEventBatchTransport> logger;
    private readonly SemaphoreSlim connectGate = new(1, 1);
    private ConnectionMultiplexer multiplexer;
    private IDatabase db;
    private DateTime lastRetentionCheckUtc = DateTime.MinValue;

    public async Task<ShareBatchPublishResult> PublishAsync(ShareEventBatch batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var payload = JsonConvert.SerializeObject(batch);
        var database = await GetDatabaseAsync(ct);
        var messageId = await database.StreamAddAsync(
            brokerConfig.StreamName,
            new[] { new NameValueEntry("payload", payload) });

        await CheckStreamLengthWarningAsync(database);

        return new ShareBatchPublishResult
        {
            Published = true,
            BrokerMessageId = messageId
        };
    }

    public async ValueTask DisposeAsync()
    {
        if(multiplexer != null)
        {
            await multiplexer.CloseAsync();
            multiplexer.Dispose();
        }
    }

    private async Task<IDatabase> GetDatabaseAsync(CancellationToken ct)
    {
        if(db != null && multiplexer?.IsConnected == true)
            return db;

        await connectGate.WaitAsync(ct);
        try
        {
            if(db != null && multiplexer?.IsConnected == true)
                return db;

            Connect();
            return db;
        }
        finally
        {
            connectGate.Release();
        }
    }

    private void Connect()
    {
        multiplexer?.Dispose();
        multiplexer = ConnectionMultiplexer.Connect(brokerConfig.ConnectionString);
        db = multiplexer.GetDatabase();
    }

    private async Task CheckStreamLengthWarningAsync(IDatabase database)
    {
        if(retentionConfig.SoftStreamLengthWarning <= 0 && retentionConfig.CriticalStreamLengthWarning <= 0)
            return;

        var now = DateTime.UtcNow;
        if(now - lastRetentionCheckUtc < TimeSpan.FromSeconds(30))
            return;

        lastRetentionCheckUtc = now;

        try
        {
            var length = await database.StreamLengthAsync(brokerConfig.StreamName);

            if(retentionConfig.CriticalStreamLengthWarning > 0 &&
               length >= retentionConfig.CriticalStreamLengthWarning)
                logger.LogCritical(
                    "Redis stream {StreamName} length is critical: {Length} >= {Threshold}. Check DbWriter/LiveAggregator consumer lag and stream retention planning.",
                    brokerConfig.StreamName, length, retentionConfig.CriticalStreamLengthWarning);
            else if(retentionConfig.SoftStreamLengthWarning > 0 &&
                    length >= retentionConfig.SoftStreamLengthWarning)
                logger.LogWarning(
                    "Redis stream {StreamName} length is high: {Length} >= {Threshold}. Check DbWriter/LiveAggregator consumer lag and stream retention planning.",
                    brokerConfig.StreamName, length, retentionConfig.SoftStreamLengthWarning);
        }
        catch(Exception ex)
        {
            logger.LogDebug(ex, "Failed to check Redis stream {StreamName} length warning threshold", brokerConfig.StreamName);
        }
    }
}
