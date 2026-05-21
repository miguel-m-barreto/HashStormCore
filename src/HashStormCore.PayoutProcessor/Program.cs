using HashStormCore.PayoutProcessor.Configuration;
using HashStormCore.PayoutProcessor.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var poolCoreConfigPath = Environment.GetEnvironmentVariable("HASHSTORM_CONFIG");
builder.Configuration
    .AddJsonFile(string.IsNullOrWhiteSpace(poolCoreConfigPath) ? "configs/config.json" : poolCoreConfigPath, true)
    .AddJsonFile("configs/payout-processor.json", true)
    .AddEnvironmentVariables("HASHSTORM_PAYOUT_");

var sidecarConfiguration = new ConfigurationBuilder()
    .AddJsonFile("configs/payout-processor.json", true)
    .AddEnvironmentVariables("HASHSTORM_PAYOUT_")
    .Build();

var config = new PayoutProcessorConfig();
sidecarConfiguration.Bind(config);

builder.Services.AddSingleton(config);
builder.Services.AddHostedService<PayoutProcessorService>();

await builder.Build().RunAsync();
