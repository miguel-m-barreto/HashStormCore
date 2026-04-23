using System;
using System.Linq;
using System.Text;
using HashStormCore.Blockchain.Bitcoin.DaemonResponses;
using HashStormCore.Blockchain.Equihash;
using HashStormCore.Blockchain.Equihash.Custom.BitcoinGold;
using HashStormCore.Blockchain.Equihash.Custom.BitcoinZ;
using HashStormCore.Blockchain.Equihash.Custom.Piratechain;
using HashStormCore.Blockchain.Equihash.Custom.Veruscoin;
using HashStormCore.Blockchain.Equihash.DaemonResponses;
using HashStormCore.Configuration;
using HashStormCore.Crypto.Hashing.Equihash;
using HashStormCore.Extensions;
using HashStormCore.Tests.Util;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HashStormCore.Tests.Blockchain.Equihash;

public class EquihashCompatibilityTests
{
    [Fact]
    public void BitcoinGoldHeaderSerialization_EncodesHeightInHashReservedPrefix()
    {
        var job = new TestBitcoinGoldJob();
        var blockTemplate = CreateBaseBlockTemplate(height: 123456, version: 4);
        var merkleRoot = Enumerable.Repeat((byte) 0x42, 32).ToArray();

        job.SetHeaderState(blockTemplate, merkleRoot);

        var headerBytes = job.SerializeHeaderForTest(1700000000u, new string('0', 64));
        var header = new EquihashBlockHeader(headerBytes);

        Assert.Equal(BitConverter.GetBytes(blockTemplate.Height), header.HashReserved.Take(4).ToArray());
        Assert.True(header.HashReserved.Skip(4).All(x => x == 0));
    }

    [Fact]
    public void PiratechainSerializeBlock_UsesSingleByteTransactionCountForSmallBlocks()
    {
        var job = new TestPiratechainJob();
        var blockTemplate = new EquihashBlockTemplate
        {
            Transactions =
            [
                new BitcoinBlockTransaction { Data = "aa" },
                new BitcoinBlockTransaction { Data = "bb" },
            ]
        };

        var result = job.SerializeBlockForTest(blockTemplate,
            [0x10, 0x20],
            [0x50],
            [0x30, 0x40]);

        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40, 0x03, 0x50, 0xaa, 0xbb }, result);
    }

    [Fact]
    public void BitcoinZInit_RebuildsCoinbaseAndMerkleWhenCoinbaseTagIsConfigured()
    {
        var coin = CreateBitcoinZTemplate();
        var blockTemplate = CreateBaseBlockTemplate(height: 2, version: 4);
        blockTemplate.FinalSaplingRootHash = new string('1', 64);

        var tagged = new TestBitcoinZJob();
        tagged.Init(blockTemplate, "job-tagged",
            new PoolConfig { Template = coin },
            new ClusterConfig
            {
                PaymentProcessing = new ClusterPaymentProcessingConfig
                {
                    CoinbaseString = "ZTAG"
                }
            },
            MockMasterClock.FromTicks(638010200200475015),
            CreatePoolDestination(),
            Network.Main,
            new DummyEquihashSolver());

        var untagged = new TestBitcoinZJob();
        untagged.Init(blockTemplate, "job-untagged",
            new PoolConfig { Template = coin },
            new ClusterConfig(),
            MockMasterClock.FromTicks(638010200200475015),
            CreatePoolDestination(),
            Network.Main,
            new DummyEquihashSolver());

        var tagBytes = Encoding.ASCII.GetBytes("ZTAG");

        Assert.True(ContainsSequence(tagged.CoinbaseInitialBytes, tagBytes));
        Assert.False(ContainsSequence(untagged.CoinbaseInitialBytes, tagBytes));
        Assert.NotEqual(untagged.CoinbaseInitialBytes.ToHexString(), tagged.CoinbaseInitialBytes.ToHexString());
        Assert.NotEqual(untagged.MerkleRootReversedHex, tagged.MerkleRootReversedHex);
    }

    [Fact]
    public void VeruscoinInit_UsesDaemonCoinbaseWhenPbaasIsActive()
    {
        var coin = CreateVeruscoinTemplate();
        var blockTemplate = CreateBaseBlockTemplate(height: 10, version: 4);
        blockTemplate.FinalSaplingRootHash = new string('2', 64);
        blockTemplate.Solution = "07000000";
        blockTemplate.CoinbaseTx = new EquihashCoinbaseTransaction
        {
            Data = "aabbccdd",
            Hash = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"
        };

        var job = new TestVeruscoinJob();
        job.Init(blockTemplate, "job-vrsc",
            new PoolConfig { Template = coin },
            new ClusterConfig(),
            MockMasterClock.FromTicks(638010200200475015),
            CreatePoolDestination(),
            Network.Main,
            new DummyEquihashSolver());

        Assert.True(job.IsPbaasActive);
        Assert.Equal(blockTemplate.CoinbaseTx.Data.HexToByteArray(), job.CoinbaseInitialBytes);
        Assert.Equal(blockTemplate.CoinbaseTx.Hash.HexToReverseByteArray(), job.CoinbaseInitialHashBytes);
    }

    private static EquihashBlockTemplate CreateBaseBlockTemplate(uint height, uint version)
    {
        return new EquihashBlockTemplate
        {
            Version = version,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5000000000,
            Target = "0007ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            NonceRange = "00000000ffffffff",
            CurTime = 1700000000,
            Bits = "1d00ffff",
            Height = height,
            Transactions = [],
        };
    }

    private static KeyId CreatePoolDestination()
    {
        return new KeyId(new uint160("00112233445566778899aabbccddeeff00112233"));
    }

    private static EquihashCoinTemplate CreateBitcoinZTemplate()
    {
        return new EquihashCoinTemplate
        {
            Name = "BitcoinZ",
            Symbol = "BTCZ",
            Family = CoinFamily.Equihash,
            UsesZCashAddressFormat = true,
            Networks = new()
            {
                ["main"] = new EquihashCoinTemplate.EquihashNetworkParams
                {
                    Diff1 = "0007FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
                    SolutionSize = 100,
                    SolutionPreambleSize = 1,
                    Solver = CreateSolverDefinition(144, 5, "BitcoinZ"),
                    CoinbaseTxNetwork = "main",
                    PayFoundersReward = false,
                    OverwinterActivationHeight = 1,
                    OverwinterTxVersion = 3,
                    OverwinterTxVersionGroupId = 63210096,
                    SaplingActivationHeight = 1,
                    SaplingTxVersion = 4,
                    SaplingTxVersionGroupId = 2301567109,
                }
            }
        };
    }

    private static EquihashCoinTemplate CreateVeruscoinTemplate()
    {
        return new EquihashCoinTemplate
        {
            Name = "Veruscoin",
            Symbol = "VRSC",
            Family = CoinFamily.Equihash,
            UsesZCashAddressFormat = false,
            Networks = new()
            {
                ["main"] = new EquihashCoinTemplate.EquihashNetworkParams
                {
                    Diff1 = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f",
                    SolutionSize = 1344,
                    SolutionPreambleSize = 3,
                    Solver = CreateSolverDefinition(200, 9, "Verushash"),
                    CoinbaseTxNetwork = "main",
                    PayFoundersReward = false,
                    OverwinterActivationHeight = 1,
                    OverwinterTxVersion = 3,
                    OverwinterTxVersionGroupId = 63210096,
                    SaplingActivationHeight = 1,
                    SaplingTxVersion = 4,
                    SaplingTxVersionGroupId = 2301567109,
                }
            }
        };
    }

    private static JObject CreateSolverDefinition(int n, int k, string personalization)
    {
        return JObject.Parse($$"""
        {
          "hash": "equihash",
          "args": [{{n}}, {{k}}, "{{personalization}}"]
        }
        """);
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private sealed class DummyEquihashSolver : EquihashSolver
    {
        public override bool Verify(ReadOnlySpan<byte> header, ReadOnlySpan<byte> solution)
        {
            return true;
        }
    }

    private sealed class TestBitcoinGoldJob : BitcoinGoldJob
    {
        public void SetHeaderState(EquihashBlockTemplate blockTemplate, byte[] testMerkleRoot)
        {
            BlockTemplate = blockTemplate;
            merkleRoot = testMerkleRoot;
        }

        public byte[] SerializeHeaderForTest(uint nTime, string nonce)
        {
            return SerializeHeader(nTime, nonce);
        }
    }

    private sealed class TestPiratechainJob : PiratechainJob
    {
        public byte[] SerializeBlockForTest(EquihashBlockTemplate blockTemplate, byte[] header, byte[] coinbase, byte[] solution)
        {
            BlockTemplate = blockTemplate;
            return SerializeBlock(header, coinbase, solution);
        }
    }

    private sealed class TestBitcoinZJob : BitcoinZJob
    {
        public byte[] CoinbaseInitialBytes => coinbaseInitial;
        public string MerkleRootReversedHex => merkleRootReversedHex;
    }

    private sealed class TestVeruscoinJob : VeruscoinJob
    {
        public byte[] CoinbaseInitialBytes => coinbaseInitial;
        public byte[] CoinbaseInitialHashBytes => coinbaseInitialHash;
        public bool IsPbaasActive => isPBaaSActive;
    }
}
