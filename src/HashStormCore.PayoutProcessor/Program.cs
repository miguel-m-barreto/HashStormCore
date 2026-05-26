using HashStormCore.PayoutProcessor.Configuration;
using HashStormCore.PayoutProcessor.Services;
using HashStormCore.Payouts.Alephium;
using HashStormCore.Payouts.Beam;
using HashStormCore.Payouts.Bitcoin;
using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Conceal;
using HashStormCore.Payouts.Cryptonote;
using HashStormCore.Payouts.Equihash;
using HashStormCore.Payouts.Ergo;
using HashStormCore.Payouts.Ethereum;
using HashStormCore.Payouts.Handshake;
using HashStormCore.Payouts.Kaspa;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Payouts.Warthog;
using HashStormCore.Payouts.Xelis;
using HashStormCore.Payouts.Zano;
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
builder.Services.AddSingleton<ICoinMetadataRegistry>(_ => new CoinsJsonCoinMetadataRegistry(ResolveCoinsJsonPath(config)));
builder.Services.AddSingleton<IPayoutProfileProvider, BitcoinPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, HandshakePayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, EquihashPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, CryptonotePayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, ConcealPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, ZanoPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, EthereumPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, ErgoPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, BeamPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, AlephiumPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, KaspaPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, XelisPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileProvider, WarthogPayoutProfileProvider>();
builder.Services.AddSingleton<IPayoutProfileResolver, PayoutProfileResolver>();
builder.Services.AddSingleton<IConnectionFactory>(_ => new PgConnectionFactory(config.PostgresConnectionString));
builder.Services.AddSingleton<IPayoutIntentRepository, PayoutIntentRepository>();
builder.Services.AddSingleton<IPayoutReservationRepository, PayoutReservationRepository>();
builder.Services.AddSingleton<IPayoutSettlementRepository, PayoutSettlementRepository>();
builder.Services.AddSingleton<PayoutReservationService>();
builder.Services.AddSingleton<IPayoutReservationRunner, DbPayoutReservationRunner>();
builder.Services.AddSingleton<PayoutSendAttemptPlannerService>();
builder.Services.AddSingleton<IPayoutPlanningRunner, DbPayoutPlanningRunner>();
builder.Services.AddSingleton<PayoutSendExecutorService>();
builder.Services.AddSingleton<IPayoutAttemptSenderRegistry>(_ =>
    new PayoutAttemptSenderRegistry(Array.Empty<PayoutAttemptSenderRegistration>()));
builder.Services.AddSingleton<IPayoutExecutionRunner, DbPayoutExecutionRunner>();
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

static string ResolveCoinsJsonPath(PayoutProcessorConfig config)
{
    if(!string.IsNullOrWhiteSpace(config.CoinsJsonPath))
        return config.CoinsJsonPath;

    const string repositoryRelativePath = "src/HashStormCore/coins.json";

    if(File.Exists(repositoryRelativePath))
        return repositoryRelativePath;

    return Path.Combine(AppContext.BaseDirectory, "coins.json");
}
