using HashStormCore.PayoutProcessor.Configuration;
using HashStormCore.PayoutProcessor.Services;
using HashStormCore.Payments;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Postgres;
using HashStormCore.Persistence.Postgres.Repositories;
using HashStormCore.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var poolCoreConfigPath = Environment.GetEnvironmentVariable("HASHSTORM_CONFIG");
var clusterConfigPath = string.IsNullOrWhiteSpace(poolCoreConfigPath) ? "configs/config.json" : poolCoreConfigPath;

builder.Configuration
    .AddJsonFile(clusterConfigPath, true)
    .AddJsonFile("configs/payout-processor.json", true)
    .AddEnvironmentVariables("HASHSTORM_PAYOUT_");

var clusterConfiguration = new ConfigurationBuilder()
    .AddJsonFile(clusterConfigPath, true)
    .Build();
var clusterConfig = new PayoutProcessorClusterConfig();
clusterConfiguration.Bind(clusterConfig);

var sidecarConfiguration = new ConfigurationBuilder()
    .AddJsonFile("configs/payout-processor.json", true)
    .AddEnvironmentVariables("HASHSTORM_PAYOUT_")
    .Build();

var config = new PayoutProcessorConfig();
sidecarConfiguration.Bind(config);
ApplyPoolCoreDefaults(clusterConfiguration, config);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(clusterConfig);
builder.Services.AddSingleton<IConnectionFactory>(_ => new PgConnectionFactory(config.PostgresConnectionString));
builder.Services.AddSingleton<IPayoutIntentRepository, PayoutIntentRepository>();
builder.Services.AddSingleton<IPayoutReservationRepository, PayoutReservationRepository>();
builder.Services.AddSingleton<IPayoutSettlementRepository, PayoutSettlementRepository>();
builder.Services.AddSingleton<PayoutReservationService>();
builder.Services.AddSingleton<PayoutSendAttemptPlannerService>();
builder.Services.AddSingleton<PayoutSendExecutorService>();
builder.Services.AddSingleton<PayoutStaleSendReconciliationService>();
builder.Services.AddSingleton<PayoutOperationIdReconciliationService>();
builder.Services.AddSingleton<PayoutAmbiguousReviewService>();
builder.Services.AddSingleton<PayoutSettlementService>();
builder.Services.AddSingleton<PayoutPoolOrchestrator>();
builder.Services.AddHostedService<PayoutProcessorService>();

await builder.Build().RunAsync();

static void ApplyPoolCoreDefaults(IConfiguration configuration, PayoutProcessorConfig config)
{
    if(string.IsNullOrWhiteSpace(config.PostgresConnectionString))
        config.PostgresConnectionString = BuildPostgresConnectionString(configuration);
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
