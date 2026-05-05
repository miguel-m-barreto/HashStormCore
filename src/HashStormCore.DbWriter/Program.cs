using HashStormCore.DbWriter.Configuration;
using HashStormCore.DbWriter.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var poolCoreConfigPath = Environment.GetEnvironmentVariable("HASHSTORM_CONFIG");
builder.Configuration
    .AddJsonFile(string.IsNullOrWhiteSpace(poolCoreConfigPath) ? "configs/config.json" : poolCoreConfigPath, true)
    .AddJsonFile("configs/db-writer.json", true)
    .AddEnvironmentVariables("HASHSTORM_DBWRITER_");

var config = new DbWriterConfig();
ApplyPoolCoreDefaults(builder.Configuration, config);
builder.Configuration.Bind(config);

builder.Services.AddSingleton(config);
builder.Services.AddHostedService<DbWriterService>();

await builder.Build().RunAsync();

static void ApplyPoolCoreDefaults(IConfiguration configuration, DbWriterConfig config)
{
    var broker = configuration.GetSection("eventPipeline:broker");

    if(IsDefaultRedisConnectionString(config.RedisConnectionString) && !string.IsNullOrWhiteSpace(broker["connectionString"]))
        config.RedisConnectionString = broker["connectionString"]!;

    if(config.StreamName == "hashstorm:share-events" && !string.IsNullOrWhiteSpace(broker["streamName"]))
        config.StreamName = broker["streamName"]!;

    if(config.PendingMinIdleMs == 60000 && int.TryParse(broker["pendingMinIdleMs"], out var pendingMinIdleMs))
        config.PendingMinIdleMs = pendingMinIdleMs;

    if(string.IsNullOrWhiteSpace(config.PostgresConnectionString))
        config.PostgresConnectionString = BuildPostgresConnectionString(configuration);
}

static bool IsDefaultRedisConnectionString(string connectionString)
{
    return connectionString is "localhost:6379" or "localhost:6379,abortConnect=false";
}

static string BuildPostgresConnectionString(IConfiguration configuration)
{
    var postgres = configuration.GetSection("persistence:postgres");
    var host = postgres["host"];
    var database = postgres["database"];
    var username = postgres["user"];

    if(string.IsNullOrWhiteSpace(host) ||
       string.IsNullOrWhiteSpace(database) ||
       string.IsNullOrWhiteSpace(username))
        return string.Empty;

    var connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = int.TryParse(postgres["port"], out var port) ? port : 5432,
        Database = database,
        Username = username,
        Password = postgres["password"],
        CommandTimeout = int.TryParse(postgres["commandTimeout"], out var commandTimeout) ? commandTimeout : 300
    };

    if(bool.TryParse(postgres["tls"], out var tls) && tls)
        connectionString.SslMode = SslMode.Require;

    return connectionString.ToString();
}
