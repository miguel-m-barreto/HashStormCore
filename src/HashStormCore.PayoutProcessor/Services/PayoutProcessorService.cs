using HashStormCore.PayoutProcessor.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HashStormCore.PayoutProcessor.Services;

public class PayoutProcessorService : BackgroundService
{
    public PayoutProcessorService(PayoutProcessorConfig config, ILogger<PayoutProcessorService> logger)
    {
        this.config = config;
        this.logger = logger;
    }

    private readonly PayoutProcessorConfig config;
    private readonly ILogger<PayoutProcessorService> logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!config.Enabled)
        {
            logger.LogInformation("PayoutProcessor sidecar is disabled");
            return Task.CompletedTask;
        }

        logger.LogWarning("PayoutProcessor sidecar is enabled, but operational payout loops are not implemented in this skeleton");
        return Task.CompletedTask;
    }
}
