using BenchmarkDotNet.Attributes;
using HashStormCore.Blockchain;
using HashStormCore.Blockchain.Bitcoin;
using HashStormCore.Extensions;
using HashStormCore.JsonRpc;
using HashStormCore.Stratum;
using HashStormCore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace HashStormCore.Benchmarks.MiningSubmit;

[MemoryDiagnoser]
[MinIterationTime(250)]
[InvocationCount(1)]
[UnrollFactor(1)]
[CategoriesColumn]
[BenchmarkCategory("MiningSubmit", "Bitcoin", "SubmitEndToEnd")]
public class BitcoinSubmitEndToEndBenchmarks
{
    private const string LegacySubmitJson =
        "{\"id\":4,\"method\":\"mining.submit\",\"params\":[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"1\",\"01000000\",\"63445774\",\"51036775\"]}";

    private const string VersionRollingSubmitJson =
        "{\"id\":4,\"method\":\"mining.submit\",\"params\":[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"1\",\"01000000\",\"63445774\",\"51036775\",\"00002000\"]}";

    private static readonly JsonSerializer Serializer = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private BitcoinSubmitBenchmarkFixture.BenchmarkBitcoinJob job = null!;
    private StratumConnection worker = null!;

    [IterationSetup]
    public void SetupInvocation()
    {
        job = BitcoinSubmitBenchmarkFixture.CreateJob();
        worker = BitcoinSubmitBenchmarkFixture.CreateWorker();
    }

    [Benchmark]
    public Share Production_Submit_EndToEnd_LegacyAccepted_PreResponse()
    {
        return job.ProcessShare(
            worker,
            BitcoinSubmitBenchmarkFixture.ExtraNonce2,
            BitcoinSubmitBenchmarkFixture.NTime,
            BitcoinSubmitBenchmarkFixture.Nonce).Share;
    }

    [Benchmark]
    public Share Production_Submit_EndToEnd_VersionRollingAccepted_PreResponse()
    {
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x0000f000;

        return job.ProcessShare(
            worker,
            BitcoinSubmitBenchmarkFixture.ExtraNonce2,
            BitcoinSubmitBenchmarkFixture.NTime,
            BitcoinSubmitBenchmarkFixture.Nonce,
            BitcoinSubmitBenchmarkFixture.VersionBits).Share;
    }

    [Benchmark]
    public int Production_Submit_EndToEnd_LegacyAccepted_TotalWithResponse()
    {
        var share = job.ProcessShare(
            worker,
            BitcoinSubmitBenchmarkFixture.ExtraNonce2,
            BitcoinSubmitBenchmarkFixture.NTime,
            BitcoinSubmitBenchmarkFixture.Nonce).Share;

        return SerializeResponse(new JsonRpcResponse<object>(true, 4)).Length + (int) share.BlockHeight;
    }

    [Benchmark]
    public int Production_Submit_EndToEnd_VersionRollingAccepted_TotalWithResponse()
    {
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x0000f000;
        var share = job.ProcessShare(
            worker,
            BitcoinSubmitBenchmarkFixture.ExtraNonce2,
            BitcoinSubmitBenchmarkFixture.NTime,
            BitcoinSubmitBenchmarkFixture.Nonce,
            BitcoinSubmitBenchmarkFixture.VersionBits).Share;

        return SerializeResponse(new JsonRpcResponse<object>(true, 4)).Length + (int) share.BlockHeight;
    }

    [Benchmark]
    public int Production_Submit_EndToEnd_LegacyJsonToResponse_TotalWithResponse()
    {
        var request = DeserializeRequest(LegacySubmitJson);
        var parameters = BitcoinJobManager.ExtractSubmitParameters(
            request.ParamsAs<object[]>(),
            versionRollingNegotiated: false);
        var share = job.ProcessShare(worker, parameters.ExtraNonce2, parameters.NTime, parameters.Nonce, parameters.VersionBits).Share;

        return SerializeResponse(new JsonRpcResponse<object>(true, request.Id)).Length + (int) share.BlockHeight;
    }

    [Benchmark]
    public int Production_Submit_EndToEnd_VersionRollingJsonToResponse_TotalWithResponse()
    {
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x0000f000;
        var request = DeserializeRequest(VersionRollingSubmitJson);
        var parameters = BitcoinJobManager.ExtractSubmitParameters(
            request.ParamsAs<object[]>(),
            versionRollingNegotiated: true);
        var share = job.ProcessShare(worker, parameters.ExtraNonce2, parameters.NTime, parameters.Nonce, parameters.VersionBits).Share;

        return SerializeResponse(new JsonRpcResponse<object>(true, request.Id)).Length + (int) share.BlockHeight;
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_Submit_EndToEnd_DuplicateRejected_TotalWithResponse()
    {
        _ = job.ProcessShare(
            worker,
            BitcoinSubmitBenchmarkFixture.ExtraNonce2,
            BitcoinSubmitBenchmarkFixture.NTime,
            BitcoinSubmitBenchmarkFixture.Nonce);

        return CatchSubmitException(() => job.ProcessShare(
            worker,
            BitcoinSubmitBenchmarkFixture.ExtraNonce2,
            BitcoinSubmitBenchmarkFixture.NTime,
            BitcoinSubmitBenchmarkFixture.Nonce));
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_Submit_EndToEnd_LowDifficultyRejected_TotalWithResponse()
    {
        worker.ContextAs<BitcoinWorkerContext>().Difficulty = double.MaxValue;

        return CatchSubmitException(() => job.ProcessShare(
            worker,
            BitcoinSubmitBenchmarkFixture.ExtraNonce2,
            BitcoinSubmitBenchmarkFixture.NTime,
            BitcoinSubmitBenchmarkFixture.Nonce));
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_Submit_EndToEnd_StaleJobRejected_BoundaryOnly()
    {
        var context = worker.ContextAs<BitcoinWorkerContext>();
        context.AddJob(job, 1);
        return context.GetJob("missing-job") == null ? 1 : 0;
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_Submit_EndToEnd_MalformedNTime_ExceptionPath()
    {
        return CatchSubmitException(() => job.ProcessShare(
            worker,
            BitcoinSubmitBenchmarkFixture.ExtraNonce2,
            "zzzzzzzz",
            BitcoinSubmitBenchmarkFixture.Nonce));
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_Submit_EndToEnd_MalformedNonce_ExceptionPath()
    {
        return CatchSubmitException(() => job.ProcessShare(
            worker,
            BitcoinSubmitBenchmarkFixture.ExtraNonce2,
            BitcoinSubmitBenchmarkFixture.NTime,
            "zzzzzzzz"));
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_Submit_EndToEnd_MalformedVersionBits_ExceptionPath()
    {
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x0000f000;

        return CatchSubmitException(() => job.ProcessShare(
            worker,
            BitcoinSubmitBenchmarkFixture.ExtraNonce2,
            BitcoinSubmitBenchmarkFixture.NTime,
            BitcoinSubmitBenchmarkFixture.Nonce,
            "zzzzzzzz"));
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_Submit_EndToEnd_MalformedExtraNonce2_ExceptionPath()
    {
        return CatchSubmitException(() => job.ProcessShare(
            worker,
            "zzzzzzzz",
            BitcoinSubmitBenchmarkFixture.NTime,
            BitcoinSubmitBenchmarkFixture.Nonce));
    }

    private static int CatchSubmitException(Func<(Share Share, string BlockHex)> action)
    {
        try
        {
            _ = action();
            return 0;
        }
        catch(StratumException ex)
        {
            return SerializeResponse(new JsonRpcResponse(new JsonRpcError((int) ex.Code, ex.Message, null), 4, false)).Length;
        }
    }

    private static string SerializeResponse(object response)
    {
        using var writer = new StringWriter();
        Serializer.Serialize(writer, response);
        return writer.ToString();
    }

    private static JsonRpcRequest DeserializeRequest(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json));
        var request = Serializer.Deserialize<JsonRpcRequest>(reader);
        return request ?? throw new JsonException("Unable to deserialize request");
    }
}

[MemoryDiagnoser]
[MinIterationTime(250)]
[CategoriesColumn]
[BenchmarkCategory("MiningSubmit", "Bitcoin", "HeaderConstruction")]
public class BitcoinSubmitHeaderConstructionBenchmarks
{
    private BitcoinSubmitBenchmarkFixture.BenchmarkBitcoinJob job = null!;
    private StratumConnection worker = null!;
    private byte[] coinbaseHash = null!;

    [GlobalSetup]
    public void Setup()
    {
        job = BitcoinSubmitBenchmarkFixture.CreateJob();
        worker = BitcoinSubmitBenchmarkFixture.CreateWorker();
        coinbaseHash = new byte[32];

        for(var i = 0; i < coinbaseHash.Length; i++)
            coinbaseHash[i] = (byte) i;
    }

    [Benchmark]
    public byte[] Production_Coinbase_Construct()
    {
        return job.ConstructCoinbase(BitcoinSubmitBenchmarkFixture.ExtraNonce1, BitcoinSubmitBenchmarkFixture.ExtraNonce2);
    }

    [Benchmark]
    public byte[] Production_BlockHeader_Construct()
    {
        return job.ConstructHeader(coinbaseHash, 0x63445774, 0x51036775, null, null);
    }

    [Benchmark]
    public byte[] Production_MerkleRoot_Construct_AsPartOfHeader()
    {
        return job.ConstructHeader(coinbaseHash, 0x63445774, 0x51036775, null, null);
    }

    [Benchmark]
    public byte[] Production_Submit_PreHashInput_Construct()
    {
        var coinbase = job.ConstructCoinbase(BitcoinSubmitBenchmarkFixture.ExtraNonce1, BitcoinSubmitBenchmarkFixture.ExtraNonce2);
        coinbaseHash[0] = coinbase[0];
        return job.ConstructHeader(coinbaseHash, 0x63445774, 0x51036775, null, null);
    }

    [Benchmark]
    public byte[] Production_Submit_HeaderAndMerkle_Construct()
    {
        return job.ConstructHeader(coinbaseHash, 0x63445774, 0x51036775, null, null);
    }

    [Benchmark]
    public byte[] Production_Submit_HeaderConstruction_WithVersionRolling()
    {
        return job.ConstructHeader(coinbaseHash, 0x63445774, 0x51036775, 0x0000f000, 0x00002000);
    }

    [Benchmark]
    public byte[] Production_Submit_HeaderConstruction_Legacy()
    {
        return job.ConstructHeader(coinbaseHash, 0x63445774, 0x51036775, null, null);
    }

    [Benchmark]
    [BenchmarkCategory("NativeHashBoundary")]
    public Share Production_NativeHash_FullWrapper()
    {
        return job.ProcessInternal(worker, BitcoinSubmitBenchmarkFixture.ExtraNonce2, 0x63445774, 0x51036775, null).Share;
    }
}

[MemoryDiagnoser]
[MinIterationTime(250)]
[CategoriesColumn]
[BenchmarkCategory("MiningSubmit", "Bitcoin", "TargetComparison")]
public class BitcoinSubmitTargetComparisonBenchmarks
{
    private byte[] hashBytes = null!;
    private uint256 hashValue = null!;
    private uint256 targetValue = null!;

    [GlobalSetup]
    public void Setup()
    {
        hashBytes = Convert.FromHexString("000001d771000000000000000000000000000000000000000000000000000001");
        hashValue = new uint256(hashBytes);
        targetValue = uint256.Parse("000001d771000000000000000000000000000000000000000000000000000000");
    }

    [Benchmark]
    public Target Production_Target_CompactDecode()
    {
        return new Target(Encoders.Hex.DecodeData("1e01d771"));
    }

    [Benchmark]
    public double Production_Target_ShareComparison()
    {
        return (double) new BigRational(BitcoinConstants.Diff1, hashBytes.ToBigInteger());
    }

    [Benchmark]
    public double Production_Target_DifficultyCalculation()
    {
        var shareDiff = (double) new BigRational(BitcoinConstants.Diff1, hashBytes.ToBigInteger());
        return shareDiff / 0.0000000001d;
    }

    [Benchmark]
    public bool Production_Target_HashMeetsTarget()
    {
        return hashValue <= targetValue;
    }

    [Benchmark]
    public double Production_Target_BigIntegerPath_IfPresent()
    {
        return (double) hashBytes.ToBigInteger();
    }
}
