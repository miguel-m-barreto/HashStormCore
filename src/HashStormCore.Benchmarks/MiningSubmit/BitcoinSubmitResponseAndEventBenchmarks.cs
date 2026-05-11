using System.Threading.Tasks.Dataflow;
using BenchmarkDotNet.Attributes;
using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Mapping;
using HashStormCore.Eventing.Queue;
using HashStormCore.JsonRpc;
using HashStormCore.Stratum;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace HashStormCore.Benchmarks.MiningSubmit;

[MemoryDiagnoser]
[MinIterationTime(250)]
[InvocationCount(1)]
[UnrollFactor(1)]
[CategoriesColumn]
[BenchmarkCategory("MiningSubmit", "Bitcoin", "ResponseSendQueue")]
public class BitcoinSubmitResponseSendQueueBenchmarks
{
    private static readonly JsonSerializer Serializer = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private JsonRpcResponse<object> acceptedResponse = null!;
    private JsonRpcResponse rejectedResponse = null!;
    private BufferBlock<object> sendQueue = null!;

    [GlobalSetup]
    public void Setup()
    {
        acceptedResponse = new JsonRpcResponse<object>(true, 4);
        rejectedResponse = new JsonRpcResponse(
            new JsonRpcError((int) StratumError.DuplicateShare, "duplicate share", null),
            4,
            false);
    }

    [IterationSetup]
    public void SetupInvocation()
    {
        sendQueue = new BufferBlock<object>(new DataflowBlockOptions
        {
            EnsureOrdered = true
        });
    }

    [Benchmark]
    public JsonRpcResponse<object> Production_Response_CreateAcceptedSubmit()
    {
        return new JsonRpcResponse<object>(true, 4);
    }

    [Benchmark]
    public JsonRpcResponse Production_Response_CreateRejectedSubmit()
    {
        return new JsonRpcResponse(
            new JsonRpcError((int) StratumError.DuplicateShare, "duplicate share", null),
            4,
            false);
    }

    [Benchmark]
    public string Production_Response_SerializeAcceptedSubmit()
    {
        return Serialize(acceptedResponse);
    }

    [Benchmark]
    public string Production_Response_SerializeRejectedSubmit()
    {
        return Serialize(rejectedResponse);
    }

    [Benchmark]
    public string Production_Response_CreateAndSerializeAcceptedSubmit()
    {
        return Serialize(new JsonRpcResponse<object>(true, 4));
    }

    [Benchmark]
    public string Production_Response_CreateAndSerializeRejectedSubmit()
    {
        return Serialize(new JsonRpcResponse(
            new JsonRpcError((int) StratumError.DuplicateShare, "duplicate share", null),
            4,
            false));
    }

    [Benchmark]
    public async Task<bool> Production_SendQueue_EnqueueAcceptedResponse()
    {
        return await sendQueue.SendAsync(acceptedResponse);
    }

    [Benchmark]
    public async Task<bool> Production_SendQueue_EnqueueRejectedResponse()
    {
        return await sendQueue.SendAsync(rejectedResponse);
    }

    [Benchmark]
    public async Task<bool> Production_SendQueue_EnqueueWhenCompletedOrRejected_IfPractical()
    {
        sendQueue.Complete();
        return await sendQueue.SendAsync(acceptedResponse);
    }

    private static string Serialize(object response)
    {
        using var writer = new StringWriter();
        Serializer.Serialize(writer, response);
        return writer.ToString();
    }
}

[MemoryDiagnoser]
[MinIterationTime(250)]
[InvocationCount(1)]
[UnrollFactor(1)]
[CategoriesColumn]
[BenchmarkCategory("MiningSubmit", "Bitcoin", "EventCreation")]
public class BitcoinSubmitEventCreationBenchmarks
{
    private ShareEventSource acceptedSource = null!;
    private ShareEventSource rejectedSource = null!;
    private ShareEventSource staleSource = null!;
    private ShareEventSource blockCandidateSource = null!;
    private ShareEvent acceptedEvent = null!;
    private ShareEvent rejectedEvent = null!;
    private InMemoryShareEventQueue queue = null!;

    [GlobalSetup]
    public void Setup()
    {
        acceptedSource = CreateSource(isBlockCandidate: false);
        rejectedSource = CreateSource(isBlockCandidate: false);
        staleSource = CreateSource(isBlockCandidate: false);
        blockCandidateSource = CreateSource(isBlockCandidate: true);

        acceptedEvent = ShareEventMapper.Map(acceptedSource);
        rejectedEvent = ShareEventMapper.Map(rejectedSource, ShareEventType.ShareRejected, "duplicate share", "22", "duplicate share");
    }

    [IterationSetup]
    public void SetupInvocation()
    {
        queue = new InMemoryShareEventQueue();
    }

    [Benchmark]
    public ShareEvent Production_Event_CreateShareAccepted()
    {
        return ShareEventMapper.Map(acceptedSource);
    }

    [Benchmark]
    public ShareEvent Production_Event_CreateShareRejected()
    {
        return ShareEventMapper.Map(rejectedSource, ShareEventType.ShareRejected, "duplicate share", "22", "duplicate share");
    }

    [Benchmark]
    public ShareEvent Production_Event_CreateShareStale()
    {
        return ShareEventMapper.Map(staleSource, ShareEventType.ShareStale, "job not found", "21", "job not found");
    }

    [Benchmark]
    public ShareEvent Production_Event_CreateBlockCandidate()
    {
        return ShareEventMapper.Map(blockCandidateSource);
    }

    [Benchmark]
    public ShareEvent Production_Event_MapShareAccepted()
    {
        return ShareEventMapper.Map(acceptedSource);
    }

    [Benchmark]
    public ShareEvent Production_Event_MapShareRejected()
    {
        return ShareEventMapper.Map(rejectedSource, ShareEventType.ShareRejected, "low difficulty share", "23", "low difficulty share");
    }

    [Benchmark]
    public string Production_Event_CreateEventId()
    {
        return Guid.NewGuid().ToString("N");
    }

    [Benchmark]
    public async ValueTask Production_Event_HandoffEnqueue()
    {
        await queue.EnqueueAsync(acceptedEvent, CancellationToken.None);
    }

    [Benchmark]
    public async ValueTask Production_Event_CreateAndHandoffShareAccepted()
    {
        await queue.EnqueueAsync(ShareEventMapper.Map(acceptedSource), CancellationToken.None);
    }

    [Benchmark]
    public async ValueTask Production_Event_CreateAndHandoffShareRejected()
    {
        await queue.EnqueueAsync(
            ShareEventMapper.Map(rejectedSource, ShareEventType.ShareRejected, "duplicate share", "22", "duplicate share"),
            CancellationToken.None);
    }

    private static ShareEventSource CreateSource(bool isBlockCandidate)
    {
        return new ShareEventSource
        {
            PoolId = "benchmark-pool",
            CoinSymbol = "DASH",
            CoinFamily = "Bitcoin",
            Miner = BitcoinSubmitBenchmarkFixture.WorkerName,
            Worker = "rig-1",
            Source = "benchmark",
            Created = new DateTime(638010200200475015, DateTimeKind.Utc),
            BlockHeight = 813750,
            Difficulty = 1,
            NetworkDifficulty = 456789.123,
            ShareMultiplier = 1,
            IsBlockCandidate = isBlockCandidate,
            BlockHash = isBlockCandidate ? "000001d771000000000000000000000000000000000000000000000000000000" : string.Empty,
            IpAddress = "127.0.0.1",
            UserAgent = "cpuminer-multi/1.3.1",
            TransactionConfirmationData = isBlockCandidate ? "coinbase-tx" : string.Empty
        };
    }
}
