using HashStormCore.ApiProvider.Configuration;
using HashStormCore.ApiProvider.Services;
using Microsoft.Extensions.Configuration;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var poolCoreConfigPath = Environment.GetEnvironmentVariable("HASHSTORM_CONFIG");
builder.Configuration
    .AddJsonFile(string.IsNullOrWhiteSpace(poolCoreConfigPath) ? "configs/config.json" : poolCoreConfigPath, true)
    .AddJsonFile("configs/api-provider.json", true)
    .AddEnvironmentVariables("HASHSTORM_API_");

var config = new ApiProviderConfig();
ApplyPoolCoreDefaults(builder.Configuration, config);
builder.Configuration.Bind(config);

builder.WebHost.UseUrls(config.ListenUrl);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(_ => new RedisLiveReadService(config.RedisConnectionString));
builder.Services.AddSingleton(_ => new PostgresHistoricalReadService(config.PostgresConnectionString));
builder.Services.AddSingleton<SanitizedPoolConfigService>();
builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();
await app.RunAsync();

static void ApplyPoolCoreDefaults(IConfiguration configuration, ApiProviderConfig config)
{
    var broker = configuration.GetSection("eventPipeline:broker");

    if(IsDefaultRedisConnectionString(config.RedisConnectionString) && !string.IsNullOrWhiteSpace(broker["connectionString"]))
        config.RedisConnectionString = broker["connectionString"]!;

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
