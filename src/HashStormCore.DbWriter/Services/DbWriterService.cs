using HashStormCore.DbWriter.Configuration;
using HashStormCore.Eventing.Configuration;
using HashStormCore.Eventing.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HashStormCore.DbWriter.Services;

public class DbWriterService : BackgroundService
{
    public DbWriterService(DbWriterConfig config, ILogger<DbWriterService> logger)
    {
        this.config = config;
        this.logger = logger;
    }

    private readonly DbWriterConfig config;
    private readonly ILogger<DbWriterService> logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(string.IsNullOrWhiteSpace(config.PostgresConnectionString))
            throw new InvalidOperationException("PostgresConnectionString is required");

        var broker = new EventPipelineBrokerConfig
        {
            ConnectionString = config.RedisConnectionString,
            StreamName = config.StreamName
        };

        await using var consumer = new RedisStreamsConsumer(broker, "db-writer", config.ConsumerName);
        var persistence = new ShareBatchPersistenceService(config.PostgresConnectionString);

        await consumer.EnsureGroupAsync("0-0");
        logger.LogInformation("DbWriter online");

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
                if(record.Batch != null)
                    await persistence.PersistAsync(record.Batch.Events, config.StreamName, record.MessageId.ToString(), "db-writer", stoppingToken);

                await consumer.AcknowledgeAsync(record.MessageId);
            }
        }
    }
}
