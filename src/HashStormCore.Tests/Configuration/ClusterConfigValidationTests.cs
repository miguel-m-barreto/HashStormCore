using System.Linq;
using FluentValidation;
using HashStormCore.Configuration;
using Xunit;

namespace HashStormCore.Tests.Configuration;

public class ClusterConfigValidationTests
{
    [Fact]
    public void Validate_AllowsMinimalConfiguration_WhenPaymentProcessingIsDisabled()
    {
        var config = CreateValidClusterConfig();

        var ex = Record.Exception(() => config.Validate());

        Assert.Null(ex);
    }

    [Fact]
    public void Validate_ReportsTopLevelAndNestedValidationErrors()
    {
        var config = new ClusterConfig
        {
            PaymentProcessing = new ClusterPaymentProcessingConfig(),
            Pools =
            [
                CreatePoolConfig("pool-a"),
                new PoolConfig
                {
                    Id = "pool-a",
                    Coin = "btc",
                    Enabled = true,
                    Address = "wallet-address",
                    Daemons =
                    [
                        new DaemonEndpointConfig
                        {
                            Host = string.Empty,
                            Port = 0,
                        }
                    ]
                }
            ]
        };

        var ex = Assert.Throws<ValidationException>(() => config.Validate());
        var messages = ex.Errors.Select(x => x.ErrorMessage).ToArray();

        Assert.Contains("Duplicate pool id 'pool-a'", messages);
        Assert.Contains("Host missing or empty", messages);
        Assert.Contains("Invalid port number '0'", messages);
    }

    private static ClusterConfig CreateValidClusterConfig()
    {
        return new ClusterConfig
        {
            PaymentProcessing = new ClusterPaymentProcessingConfig(),
            Pools =
            [
                CreatePoolConfig("pool-a")
            ]
        };
    }

    private static PoolConfig CreatePoolConfig(string id)
    {
        return new PoolConfig
        {
            Id = id,
            Coin = "btc",
            Enabled = true,
            Address = "wallet-address",
            EnableInternalStratum = false,
            Daemons =
            [
                new DaemonEndpointConfig
                {
                    Host = "127.0.0.1",
                    Port = 17123,
                }
            ]
        };
    }
}
