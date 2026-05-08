using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using HashStormCore.Configuration;
using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Abstractions;
using HashStormCore.Eventing.Configuration;
using HashStormCore.Eventing.Publishing;
using HashStormCore.Eventing.Transport;
using Xunit;

namespace HashStormCore.Tests.Eventing;

public class EventPipelineSafetyTests
{
    [Fact]
    public async Task NullTransportDoesNotReportPublishSuccess()
    {
        var transport = new NullShareEventBatchTransport();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.PublishAsync(new ShareEventBatch(), CancellationToken.None));
    }

    [Fact]
    public void CriticalAcceptedShareHandoffDoesNotUsePostSendAction()
    {
        var source = ReadSource("HashStormCore", "Mining", "PoolBase.cs");
        var method = ExtractMethod(source, "PublishShareAfterResponseAsync", "PublishRejectedShareAfterResponseAsync");

        Assert.DoesNotContain("ExecuteAfterPriorSendsAsync", method);
        Assert.Contains("ShareEventMapper.Map", method);
        Assert.Contains("EnqueueShareEventOrFailAsync", method);
    }

    [Fact]
    public void TelemetryRejectedShareMayStillUsePostSendAction()
    {
        var source = ReadSource("HashStormCore", "Mining", "PoolBase.cs");
        var method = ExtractMethod(source, "PublishRejectedShareAfterResponseAsync", "EnqueueShareEventOrFailAsync");

        Assert.Contains("ExecuteAfterPriorSendsAsync", method);
        Assert.Contains("EnqueueTelemetryShareEventBestEffortAsync", method);
    }

    [Fact]
    public void AgedBitcoinSubmitRespondsAndPublishesTelemetryInsteadOfSilentDrop()
    {
        var bitcoinSource = ReadSource("HashStormCore", "Blockchain", "Bitcoin", "BitcoinPool.cs");
        var method = ExtractMethod(bitcoinSource, "OnSubmitAsync", "OnSuggestDifficultyAsync");
        var branchStart = method.IndexOf("requestAge > maxShareAge", StringComparison.Ordinal);
        var branch = method[branchStart..method.IndexOf("// check worker state", branchStart, StringComparison.Ordinal)];
        var poolBaseSource = ReadSource("HashStormCore", "Mining", "PoolBase.cs");
        var helper = ExtractMethod(poolBaseSource, "RejectAgedSubmitBeforeValidationAsync", "EnqueueShareEventOrFailAsync");

        Assert.Contains("RejectAgedSubmitBeforeValidationAsync", branch);
        Assert.Contains("request_age_exceeded", helper);
        Assert.Contains("RespondErrorAsync", helper);
        Assert.Contains("PublishSubmitRejectedShareAfterResponseAsync", helper);
        Assert.True(helper.IndexOf("RespondErrorAsync", StringComparison.Ordinal) <
            helper.IndexOf("PublishSubmitRejectedShareAfterResponseAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void AgedSubmitBranchesUseResponseAndTelemetryPathAcrossPoolFamilies()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "HashStormCore", "Blockchain");
        var files = Directory.GetFiles(root, "*Pool.cs", SearchOption.AllDirectories);

        foreach(var file in files)
        {
            var source = File.ReadAllText(file);
            var index = source.IndexOf("requestAge > maxShareAge", StringComparison.Ordinal);

            while(index >= 0)
            {
                var branch = source[index..Math.Min(source.Length, index + 1600)];

                Assert.True(
                    branch.Contains("RejectAgedSubmitBeforeValidationAsync", StringComparison.Ordinal) ||
                    branch.Contains("PublishRejectedShareAfterResponseAsync", StringComparison.Ordinal) ||
                    branch.Contains("PublishSubmitRejectedShareAfterResponseAsync", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} has an aged-submit branch without rejected/stale event telemetry");

                index = source.IndexOf("requestAge > maxShareAge", index + 1, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void RequestAgeExceededTelemetryIsClassifiedAsStale()
    {
        var source = ReadSource("HashStormCore", "Mining", "PoolBase.cs");
        var method = ExtractMethod(source, "PublishRejectedShareAfterResponseAsync", "EnqueueShareEventOrFailAsync");

        Assert.Contains("request_age_exceeded", method);
        Assert.Contains("ShareEventType.ShareStale", method);
    }

    [Fact]
    public void BeamSubmitTelemetryDoesNotDependOnSubmitSubstring()
    {
        var methodsSource = ReadSource("HashStormCore", "Blockchain", "Beam", "BeamStratumMethods.cs");
        var beamPoolSource = ReadSource("HashStormCore", "Blockchain", "Beam", "BeamPool.cs");
        var poolBaseSource = ReadSource("HashStormCore", "Mining", "PoolBase.cs");

        Assert.Contains("Submit = \"solution\"", methodsSource);
        Assert.Contains("override bool IsShareSubmitRequest", beamPoolSource);
        Assert.Contains("request?.Method == BeamStratumMethods.Submit", beamPoolSource);
        Assert.Contains("PublishSubmitRejectedShareAfterResponseAsync", beamPoolSource);
        Assert.Contains("PublishSubmitRejectedShareAfterResponseAsync", poolBaseSource);
    }

    [Fact]
    public void BitcoinSubmitAndConfigureUseStrictMinerInputValidation()
    {
        var bitcoinPoolSource = ReadSource("HashStormCore", "Blockchain", "Bitcoin", "BitcoinPool.cs");
        var bitcoinJobSource = ReadSource("HashStormCore", "Blockchain", "Bitcoin", "BitcoinJob.cs");
        var bitcoinSubmitValidationSource = ReadSource("HashStormCore", "Blockchain", "Bitcoin", "BitcoinSubmitValidation.cs");

        Assert.Contains("GetRawSubmitParams(request)", bitcoinPoolSource);
        Assert.Contains("BitcoinSubmitValidation.ParseHex8Strict(requestedMaskValue.Value<string>()", bitcoinPoolSource);
        Assert.DoesNotContain("requestedMask = uint.Parse", bitcoinPoolSource);
        Assert.Contains("BitcoinSubmitValidation.ParseSubmitInput(", bitcoinJobSource);
        Assert.Contains("BitcoinSubmitValidation.CreateDuplicateKey", bitcoinJobSource);
        Assert.Contains("TryParseHex8Strict", bitcoinSubmitValidationSource);
        Assert.DoesNotContain("uint.Parse", bitcoinSubmitValidationSource);
        Assert.DoesNotContain("string.Join", bitcoinJobSource);
    }

    [Fact]
    public void OutboxPublisherRequiresPositivePublishResultBeforeCheckpointAdvance()
    {
        var source = ReadSource("HashStormCore.Eventing", "Outbox", "ShareEventOutboxPublisher.cs");

        Assert.Contains("result?.Published != true", source);
        Assert.True(source.IndexOf("PublishWithRetryAsync(batch", StringComparison.Ordinal) <
            source.IndexOf("AdvanceCheckpointAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsumerGroupsUseHistoricalAndLiveStartIds()
    {
        var consumer = ReadSource("HashStormCore.Eventing", "Transport", "RedisStreamsConsumer.cs");
        var dbWriter = ReadSource("HashStormCore.DbWriter", "Services", "DbWriterService.cs");
        var liveAggregator = ReadSource("HashStormCore.LiveAggregator", "Services", "LiveAggregatorService.cs");

        Assert.Contains("EnsureGroupAsync(string startId = \"0-0\")", consumer);
        Assert.Contains("StreamCreateConsumerGroupAsync(brokerConfig.StreamName, groupName, startId, true)", consumer);
        Assert.Contains("EnsureGroupAsync(\"0-0\")", dbWriter);
        Assert.Contains("EnsureGroupAsync(\"$\")", liveAggregator);
        Assert.True(liveAggregator.IndexOf("EnsureGroupAsync(\"$\")", StringComparison.Ordinal) <
            liveAggregator.IndexOf("RebuildFromRetainedStreamAsync", StringComparison.Ordinal));
        Assert.Contains("ReadRangePagesAsync", liveAggregator);
    }

    [Fact]
    public void EnabledPipelineWithRedisStreamsBrokerResolvesRedisTransport()
    {
        using var container = BuildContainer("redis-streams");

        var transport = container.Resolve<IShareEventBatchTransport>();

        Assert.IsType<RedisStreamsShareEventBatchTransport>(transport);
    }

    [Fact]
    public void EnabledPipelineWithUnsupportedBrokerThrowsOnTransportResolution()
    {
        using var container = BuildContainer("typo");

        var ex = Assert.Throws<DependencyResolutionException>(() => container.Resolve<IShareEventBatchTransport>());

        Assert.Contains("Unsupported eventPipeline.broker.type", ex.ToString());
    }

    [Fact]
    public void ShareBatchPublisherIsNotRegisteredForProductionDirectPublishing()
    {
        using var container = BuildContainer("redis-streams");

        Assert.False(container.IsRegistered<ShareBatchPublisher>());
    }

    private static string ReadSource(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", Path.Combine(pathParts)));
    }

    private static IContainer BuildContainer(string brokerType)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new ClusterConfig
        {
            EventPipeline = new EventPipelineConfig
            {
                Enabled = true,
                Broker = new EventPipelineBrokerConfig
                {
                    Type = brokerType,
                    StartupRequired = false
                }
            }
        });
        builder.RegisterModule<AutofacModule>();
        return builder.Build();
    }

    private static string ExtractMethod(string source, string startMethod, string nextMethod)
    {
        var start = source.IndexOf(startMethod, StringComparison.Ordinal);
        var end = source.IndexOf(nextMethod, start + startMethod.Length, StringComparison.Ordinal);

        Assert.True(start >= 0, $"{startMethod} not found");
        Assert.True(end > start, $"{nextMethod} not found after {startMethod}");

        return source[start..end];
    }
}
