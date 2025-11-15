using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Microsoft.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HashStormCore.JsonRpc;
using HashStormCore.Stratum;
using HashStormCore.Time;
using NLog;
#pragma warning disable 8974

namespace HashStormCore.Tests.Benchmarks.Stratum;

[MemoryDiagnoser]
public class StratumConnectionBenchmarks : TestBase
{
    private const string JsonRpcVersion = "2.0";
    private const string ConnectionId = "foo";
    private const string requestString = "{\"params\": [\"slush.miner1\", \"password\"], \"id\": 42, \"method\": \"mining.authorize\"}\\n";
    private const string ProcessRequestAsyncMethod = "ProcessRequestAsync";

    private RecyclableMemoryStreamManager rmsm;
    private IMasterClock clock;
    private ILogger logger;

    private StratumConnection connection;
    private PrivateObject wrapper;

    [GlobalSetup]
    public void Setup()
    {
        ModuleInitializer.Initialize();

        rmsm = ModuleInitializer.Container.Resolve<RecyclableMemoryStreamManager>();
        clock = ModuleInitializer.Container.Resolve<IMasterClock>();
        logger = new NullLogger(LogManager.LogFactory);

        connection = new(logger, rmsm, clock, ConnectionId, false);
        wrapper = new(connection);
    }

    Task OnPlaceholderRequestAsync(StratumConnection con, JsonRpcRequest request, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    [Benchmark]
    public async Task ProcessRequest_Handle_Valid_Request()
    {
        await (Task) wrapper.Invoke(ProcessRequestAsyncMethod,
            CancellationToken.None,
            OnPlaceholderRequestAsync,
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(requestString)));
    }
}
