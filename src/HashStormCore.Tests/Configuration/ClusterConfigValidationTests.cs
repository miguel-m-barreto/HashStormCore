using System.IO;
using System.Linq;
using FluentValidation;
using HashStormCore.Configuration;
using HashStormCore.Eventing.Configuration;
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

    [Fact]
    public void Validate_AllowsRedisStreamsBroker_WhenEventPipelineEnabled()
    {
        var config = CreateValidClusterConfig();
        config.EventPipeline = new EventPipelineConfig
        {
            Enabled = true,
            Broker = new EventPipelineBrokerConfig
            {
                Type = "redis-streams"
            }
        };

        var ex = Record.Exception(() => config.Validate());

        Assert.Null(ex);
    }

    [Fact]
    public void Validate_AllowsAbsoluteOutboxDirectory_WhenEventPipelineEnabled()
    {
        var config = CreateValidClusterConfig();
        config.EventPipeline = new EventPipelineConfig
        {
            Enabled = true,
            Broker = new EventPipelineBrokerConfig
            {
                Type = "redis-streams"
            },
            Outbox = new EventPipelineOutboxConfig
            {
                Directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hashstorm-outbox-validation"))
            }
        };

        var ex = Record.Exception(() => config.Validate());

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("redis")]
    public void Validate_RejectsUnsupportedBroker_WhenEventPipelineEnabled(string brokerType)
    {
        var config = CreateValidClusterConfig();
        config.EventPipeline = new EventPipelineConfig
        {
            Enabled = true,
            Broker = new EventPipelineBrokerConfig
            {
                Type = brokerType
            }
        };

        var ex = Assert.Throws<ValidationException>(() => config.Validate());
        var messages = ex.Errors.Select(x => x.ErrorMessage).ToArray();

        Assert.Contains("eventPipeline.enabled=true requires supported eventPipeline.broker.type 'redis-streams'", messages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptyOutboxDirectory_WhenEventPipelineEnabled(string directory)
    {
        var config = CreateValidClusterConfig();
        config.EventPipeline = new EventPipelineConfig
        {
            Enabled = true,
            Broker = new EventPipelineBrokerConfig
            {
                Type = "redis-streams"
            },
            Outbox = new EventPipelineOutboxConfig
            {
                Directory = directory
            }
        };

        var ex = Assert.Throws<ValidationException>(() => config.Validate());
        var messages = ex.Errors.Select(x => x.ErrorMessage).ToArray();

        Assert.Contains("eventPipeline.enabled=true requires non-empty eventPipeline.outbox.directory", messages);
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
