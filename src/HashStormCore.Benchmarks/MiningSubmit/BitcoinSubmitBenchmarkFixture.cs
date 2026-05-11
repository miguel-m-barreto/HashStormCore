using System.Collections.Concurrent;
using System.Reflection;
using Autofac;
using Microsoft.IO;
using HashStormCore.Blockchain;
using HashStormCore.Blockchain.Bitcoin;
using HashStormCore.Blockchain.Bitcoin.DaemonResponses;
using HashStormCore.Configuration;
using HashStormCore.Crypto;
using HashStormCore.Mappings;
using HashStormCore.Stratum;
using HashStormCore.Time;
using HashStormCore.Util;
using NBitcoin;
using NBitcoin.Altcoins;
using Newtonsoft.Json;
using NLog;

namespace HashStormCore.Benchmarks.MiningSubmit;

internal static class BitcoinSubmitBenchmarkFixture
{
    public const string WorkerName = "yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4";
    public const string JobId = "1";
    public const string ExtraNonce1 = "60000001";
    public const string ExtraNonce2 = "01000000";
    public const string NTime = "63445774";
    public const string Nonce = "51036775";
    public const string VersionBits = "00002000";
    public const int ExpectedExtraNonce2Length = 8;

    private const string BlockTemplateJson =
        "{\"version\":536870912,\"previousBlockhash\":\"0000011a86a1ad3609e5359b6b6411a1654108ee7c1afc003dec23b5a0400e4b\",\"coinbaseValue\":1801475949,\"target\":\"000001d771000000000000000000000000000000000000000000000000000000\",\"nonceRange\":\"00000000ffffffff\",\"curTime\":1665423220,\"bits\":\"1e01d771\",\"height\":813750,\"transactions\":[],\"coinbaseAux\":{\"flags\":null},\"default_witness_commitment\":null,\"capabilities\":[\"proposal\"],\"rules\":[\"csv\",\"dip0001\",\"bip147\",\"dip0003\",\"dip0008\",\"realloc\",\"dip0020\",\"dip0024\"],\"vbavailable\":{},\"vbrequired\":0,\"longpollid\":\"0000011a86a1ad3609e5359b6b6411a1654108ee7c1afc003dec23b5a0400e4b814670\",\"mintime\":1665422408,\"mutable\":[\"time\",\"transactions\",\"prevblock\"],\"sigoplimit\":40000,\"sizelimit\":2000000,\"previousbits\":\"1e01bee4\",\"masternode\":[{\"payee\":\"yVXDAM73Tg6A44Bm3qduXsMCYxzuqBCT48\",\"script\":\"76a91464f2b2b84f62d68a2cd7f7f5fb2b5aa75ef716d788ac\",\"amount\":1080885569}],\"masternode_payments_started\":true,\"masternode_payments_enforced\":true,\"superblock\":[],\"superblocks_started\":true,\"superblocks_enabled\":true,\"coinbase_payload\":\"0200b66a0c00fbab6816312c05803d026cce30fec0332c059f66e421ab0bf65b96ea9efb8a22e12cfc31666208b47a006e5b74f95a4c0797b6bc620ea1cc07cb53616e547302\"}";

    private static readonly Lazy<IContainer> Container = new(BuildContainer);
    private static readonly Lazy<JsonSerializerSettings> SerializerSettings = new(() => Container.Value.Resolve<JsonSerializerSettings>());
    private static readonly Lazy<BitcoinTemplate> DashTemplate = new(() => (BitcoinTemplate) LoadCoinTemplates()["dash"]);
    private static readonly FieldInfo SubmissionsField = typeof(BitcoinJob).GetField("submissions", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static JsonSerializerSettings JsonSettings => SerializerSettings.Value;

    public static BenchmarkBitcoinJob CreateJob(double difficulty = 0.0000000001d, uint? versionRollingMask = null)
    {
        var job = new BenchmarkBitcoinJob();
        var coin = DashTemplate.Value;
        var pc = new PoolConfig
        {
            Id = "benchmark-pool",
            Template = coin
        };

        var blockTemplate = JsonConvert.DeserializeObject<BlockTemplate>(BlockTemplateJson, JsonSettings)!;
        var clock = new BenchmarkClock(new DateTime(638010200200475015, DateTimeKind.Utc));
        var network = Dash.Instance.Testnet;
        var poolAddressDestination = BitcoinUtils.AddressToDestination("yNkA6gVSPqKzW6WmJtTazRLKbSkQA5ND2h", network);

        job.Init(blockTemplate, JobId, pc, null, new ClusterConfig(), clock, poolAddressDestination, network, false,
            coin.ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue);

        return job;
    }

    public static StratumConnection CreateWorker(double difficulty = 0.0000000001d, uint? versionRollingMask = null)
    {
        var clock = new BenchmarkClock(new DateTime(638010200200475015, DateTimeKind.Utc));
        var worker = new StratumConnection(
            new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(),
            clock,
            "benchmark-worker",
            false);

        worker.SetContext(new BitcoinWorkerContext
        {
            Miner = WorkerName,
            Worker = "rig-1",
            ExtraNonce1 = ExtraNonce1,
            Difficulty = difficulty,
            VersionRollingMask = versionRollingMask,
            UserAgent = "cpuminer-multi/1.3.1"
        });

        return worker;
    }

    public static Share CreateShare(bool isBlockCandidate = false)
    {
        return new Share
        {
            PoolId = "benchmark-pool",
            Miner = WorkerName,
            Worker = "rig-1",
            UserAgent = "cpuminer-multi/1.3.1",
            IpAddress = "127.0.0.1",
            Source = "benchmark",
            Difficulty = 1,
            NetworkDifficulty = 456789.123,
            BlockHeight = 813750,
            IsBlockCandidate = isBlockCandidate,
            BlockHash = isBlockCandidate ? "000001d771000000000000000000000000000000000000000000000000000000" : string.Empty,
            Created = new DateTime(638010200200475015, DateTimeKind.Utc)
        };
    }

    public static void ResetSubmissions(BitcoinJob job)
    {
        var submissions = (ConcurrentDictionary<BitcoinSubmitDuplicateKey, bool>) SubmissionsField.GetValue(job)!;
        submissions.Clear();
    }

    private static IContainer BuildContainer()
    {
        var builder = new ContainerBuilder();
        builder.RegisterAssemblyModules(typeof(AutofacModule).GetTypeInfo().Assembly);
        builder.RegisterType<ObjectMapper>().As<IObjectMapper>().SingleInstance();
        return builder.Build();
    }

    private static Dictionary<string, CoinTemplate> LoadCoinTemplates()
    {
        var basePath = AppContext.BaseDirectory;
        var defaultDefinitions = Path.Combine(basePath, "coins.json");
        return CoinTemplateLoader.Load(Container.Value, [defaultDefinitions]);
    }

    internal sealed class BenchmarkBitcoinJob : BitcoinJob
    {
        public byte[] ConstructCoinbase(string extraNonce1, string extraNonce2)
        {
            return SerializeCoinbase(extraNonce1, extraNonce2);
        }

        public byte[] ConstructHeader(Span<byte> coinbaseHash, uint nTime, uint nonce, uint? versionMask, uint? versionBits)
        {
            return SerializeHeader(coinbaseHash, nTime, nonce, versionMask, versionBits);
        }

        public (Share Share, string BlockHex) ProcessInternal(StratumConnection worker, string extraNonce2, uint nTime, uint nonce, uint? versionBits)
        {
            return ProcessShareInternal(worker, extraNonce2, nTime, nonce, versionBits);
        }

        public bool TryRegisterSubmit(string extraNonce1, string extraNonce2, uint nTime, uint nonce, uint versionBits = 0, bool hasVersionBits = false)
        {
            return RegisterSubmit(extraNonce1, extraNonce2, nTime, nonce, versionBits, hasVersionBits);
        }
    }

    private sealed class BenchmarkClock(DateTime now) : IMasterClock
    {
        public DateTime Now { get; } = now;
    }
}
