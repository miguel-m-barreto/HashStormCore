using HashStormCore.LiveAggregator.Configuration;
using HashStormCore.LiveAggregator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var poolCoreConfigPath = Environment.GetEnvironmentVariable("HASHSTORM_CONFIG");
builder.Configuration
    .AddJsonFile(string.IsNullOrWhiteSpace(poolCoreConfigPath) ? "configs/config.json" : poolCoreConfigPath, true)
    .AddJsonFile("configs/live-aggregator.json", true)
    .AddEnvironmentVariables("HASHSTORM_LIVE_");

var config = new LiveAggregatorConfig();
ApplyPoolCoreDefaults(builder.Configuration, config);
builder.Configuration.Bind(config);

builder.Services.AddSingleton(config);
builder.Services.AddHostedService<LiveAggregatorService>();

await builder.Build().RunAsync();

static void ApplyPoolCoreDefaults(IConfiguration configuration, LiveAggregatorConfig config)
{
    var broker = configuration.GetSection("eventPipeline:broker");
    var live = configuration.GetSection("eventPipeline:live");

    if(IsDefaultRedisConnectionString(config.RedisConnectionString) && !string.IsNullOrWhiteSpace(broker["connectionString"]))
        config.RedisConnectionString = broker["connectionString"]!;

    if(config.StreamName == "hashstorm:share-events" && !string.IsNullOrWhiteSpace(broker["streamName"]))
        config.StreamName = broker["streamName"]!;

    if(config.PendingMinIdleMs == 60000 && int.TryParse(broker["pendingMinIdleMs"], out var pendingMinIdleMs))
        config.PendingMinIdleMs = pendingMinIdleMs;

    if(config.LiveWindowSeconds == 600 && int.TryParse(live["windowSeconds"], out var liveWindowSeconds))
        config.LiveWindowSeconds = liveWindowSeconds;

    if(config.RedisTtlSeconds == 900 && int.TryParse(live["redisTtlSeconds"], out var redisTtlSeconds))
        config.RedisTtlSeconds = redisTtlSeconds;

    if(config.BucketSeconds == 10 && int.TryParse(live["bucketSeconds"], out var bucketSeconds))
        config.BucketSeconds = bucketSeconds;

    if(config.Pools == null || config.Pools.Length == 0)
    {
        var poolIds = configuration
            .GetSection("pools")
            .GetChildren()
            .Select(x => x["id"])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if(poolIds.Length > 0)
            config.Pools = poolIds!;
    }
}

static bool IsDefaultRedisConnectionString(string connectionString)
{
    return connectionString is "localhost:6379" or "localhost:6379,abortConnect=false";
}
