using System;
using System.IO;
using System.Linq;
using HashStormCore.Eventing.Configuration;
using Xunit;

namespace HashStormCore.Tests.Eventing;

public class RedisStreamRetentionTests
{
    [Fact]
    public void CriticalStreamRetentionConfigDoesNotExposeHardMaxLength()
    {
        var names = typeof(EventPipelineRetentionConfig)
            .GetProperties()
            .Select(x => x.Name)
            .ToArray();

        Assert.DoesNotContain("MaxStreamLength", names);
        Assert.Contains("SoftStreamLengthWarning", names);
        Assert.Contains("CriticalStreamLengthWarning", names);
    }

    [Fact]
    public void RedisStreamTransportChecksLengthWarningsWithoutTrimming()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "HashStormCore.Eventing", "Transport", "RedisStreamsShareEventBatchTransport.cs"));

        Assert.Contains("StreamLengthAsync", source);
        Assert.Contains("TimeSpan.FromSeconds(30)", source);
        Assert.Contains("LogWarning", source);
        Assert.Contains("LogCritical", source);
        Assert.DoesNotContain("StreamTrim", source);
    }

    [Fact]
    public void RedisStreamTransportDoesNotTrimCriticalStreamOnPublish()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "HashStormCore.Eventing", "Transport", "RedisStreamsShareEventBatchTransport.cs"));

        Assert.DoesNotContain("maxLength:", source);
        Assert.DoesNotContain("useApproximateMaxLength", source);
    }

    [Fact]
    public void ExampleConfigDoesNotExposeHardStreamOrHandoffLimits()
    {
        var config = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "config", "event-pipeline.example.json"));

        Assert.DoesNotContain("maxStreamLength", config);
        Assert.DoesNotContain("maxBufferedEvents", config);
        Assert.DoesNotContain("maxBufferedBytes", config);
        Assert.DoesNotContain("overflowPolicy", config);
        Assert.Contains("\"startupRequired\": false", config);
    }

    [Fact]
    public void OutboxConfigDoesNotExposeDecorativeEnabledSwitch()
    {
        var eventingNames = typeof(EventPipelineOutboxConfig)
            .GetProperties()
            .Select(x => x.Name)
            .ToArray();

        Assert.DoesNotContain("Enabled", eventingNames);

        var config = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "config", "event-pipeline.example.json"));

        Assert.DoesNotContain("\"enabled\": true", ExtractOutboxBlock(config));
    }

    private static string ExtractOutboxBlock(string config)
    {
        var start = config.IndexOf("\"outbox\"", StringComparison.Ordinal);
        var end = config.IndexOf("\"batching\"", StringComparison.Ordinal);
        return start >= 0 && end > start ? config[start..end] : config;
    }
}
