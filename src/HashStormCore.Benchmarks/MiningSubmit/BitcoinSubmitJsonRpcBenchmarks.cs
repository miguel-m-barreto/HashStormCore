using BenchmarkDotNet.Attributes;
using HashStormCore.Blockchain.Bitcoin;
using HashStormCore.JsonRpc;
using HashStormCore.Stratum;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace HashStormCore.Benchmarks.MiningSubmit;

[MemoryDiagnoser]
[MinIterationTime(250)]
[CategoriesColumn]
[BenchmarkCategory("MiningSubmit", "Bitcoin", "JsonRpc")]
public class BitcoinSubmitJsonRpcBenchmarks
{
    private static readonly JsonSerializer Serializer = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private string legacySubmitJson = null!;
    private string versionRollingSubmitJson = null!;
    private string nonStringParamSubmitJson = null!;
    private string missingParamsSubmitJson = null!;
    private string extraParamsSubmitJson = null!;
    private string malformedJson = null!;
    private string wrongMethodJson = null!;
    private string subscribeJson = null!;
    private string authorizeJson = null!;
    private JsonRpcRequest legacySubmitRequest = null!;
    private JsonRpcRequest versionRollingSubmitRequest = null!;
    private JsonRpcRequest nonStringParamSubmitRequest = null!;
    private JsonRpcRequest missingParamsSubmitRequest = null!;
    private JsonRpcRequest extraParamsSubmitRequest = null!;

    [GlobalSetup]
    public void Setup()
    {
        legacySubmitJson = "{\"id\":4,\"method\":\"mining.submit\",\"params\":[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"1\",\"01000000\",\"63445774\",\"51036775\"]}";
        versionRollingSubmitJson = "{\"id\":4,\"method\":\"mining.submit\",\"params\":[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"1\",\"01000000\",\"63445774\",\"51036775\",\"00002000\"]}";
        nonStringParamSubmitJson = "{\"id\":4,\"method\":\"mining.submit\",\"params\":[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"1\",1,\"63445774\",\"51036775\"]}";
        missingParamsSubmitJson = "{\"id\":4,\"method\":\"mining.submit\",\"params\":[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"1\",\"01000000\"]}";
        extraParamsSubmitJson = "{\"id\":4,\"method\":\"mining.submit\",\"params\":[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"1\",\"01000000\",\"63445774\",\"51036775\",\"00002000\",\"extra\"]}";
        malformedJson = "{\"id\":4,\"method\":\"mining.submit\",\"params\":[";
        wrongMethodJson = "{\"id\":4,\"method\":\"mining.authorize\",\"params\":[\"miner\",\"x\"]}";
        subscribeJson = "{\"id\":1,\"method\":\"mining.subscribe\",\"params\":[\"cpuminer-multi/1.3.1\"]}";
        authorizeJson = "{\"id\":2,\"method\":\"mining.authorize\",\"params\":[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4.rig-1\",\"x\"]}";

        legacySubmitRequest = Deserialize(legacySubmitJson);
        versionRollingSubmitRequest = Deserialize(versionRollingSubmitJson);
        nonStringParamSubmitRequest = Deserialize(nonStringParamSubmitJson);
        missingParamsSubmitRequest = Deserialize(missingParamsSubmitJson);
        extraParamsSubmitRequest = Deserialize(extraParamsSubmitJson);
    }

    [Benchmark]
    public string Production_JsonRpc_ParseSubmit_Legacy5Params()
    {
        return Deserialize(legacySubmitJson).Method;
    }

    [Benchmark]
    public string Production_JsonRpc_ParseSubmit_VersionRolling6Params()
    {
        return Deserialize(versionRollingSubmitJson).Method;
    }

    [Benchmark]
    public int Production_JsonRpc_ParseSubmit_NonStringParam()
    {
        return Deserialize(nonStringParamSubmitJson).ParamsAs<object[]>().Length;
    }

    [Benchmark]
    public int Production_JsonRpc_ParseSubmit_MissingParams()
    {
        return Deserialize(missingParamsSubmitJson).ParamsAs<object[]>().Length;
    }

    [Benchmark]
    public int Production_JsonRpc_ParseSubmit_ExtraParams()
    {
        return Deserialize(extraParamsSubmitJson).ParamsAs<object[]>().Length;
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_JsonRpc_ParseSubmit_MalformedJson_ExceptionPath()
    {
        try
        {
            return Deserialize(malformedJson).Method.Length;
        }
        catch(JsonException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    public string Production_JsonRpc_ParseSubmit_WrongMethod()
    {
        return Deserialize(wrongMethodJson).Method;
    }

    [Benchmark]
    public string Production_JsonRpc_ParseSubscribe()
    {
        return Deserialize(subscribeJson).Method;
    }

    [Benchmark]
    public string Production_JsonRpc_ParseAuthorize()
    {
        return Deserialize(authorizeJson).Method;
    }

    [Benchmark]
    public string Production_JsonRpc_ExtractSubmitParams_Legacy5Params()
    {
        var parameters = BitcoinJobManager.ExtractSubmitParameters(
            legacySubmitRequest.ParamsAs<object[]>(),
            versionRollingNegotiated: false);

        return parameters.Nonce;
    }

    [Benchmark]
    public string Production_JsonRpc_ExtractSubmitParams_VersionRolling6Params()
    {
        var parameters = BitcoinJobManager.ExtractSubmitParameters(
            versionRollingSubmitRequest.ParamsAs<object[]>(),
            versionRollingNegotiated: true);

        return parameters.VersionBits;
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_JsonRpc_ExtractSubmitParams_NonStringParam_ExceptionPath()
    {
        try
        {
            _ = BitcoinJobManager.ExtractSubmitParameters(
                nonStringParamSubmitRequest.ParamsAs<object[]>(),
                versionRollingNegotiated: false);
            return 0;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_JsonRpc_ExtractSubmitParams_MissingParams_ExceptionPath()
    {
        try
        {
            _ = BitcoinJobManager.ExtractSubmitParameters(
                missingParamsSubmitRequest.ParamsAs<object[]>(),
                versionRollingNegotiated: false);
            return 0;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("ExceptionPath")]
    public int Production_JsonRpc_ExtractSubmitParams_ExtraParams_ExceptionPath()
    {
        try
        {
            _ = BitcoinJobManager.ExtractSubmitParameters(
                extraParamsSubmitRequest.ParamsAs<object[]>(),
                versionRollingNegotiated: true);
            return 0;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    private static JsonRpcRequest Deserialize(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json));
        var request = Serializer.Deserialize<JsonRpcRequest>(reader);
        return request ?? throw new JsonException("Unable to deserialize request");
    }
}
