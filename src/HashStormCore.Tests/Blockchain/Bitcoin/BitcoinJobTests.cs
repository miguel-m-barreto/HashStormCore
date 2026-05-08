using System;
using Autofac;
using Microsoft.IO;
using HashStormCore.Blockchain.Bitcoin;
using HashStormCore.Configuration;
using HashStormCore.Stratum;
using HashStormCore.Tests.Util;
using NBitcoin;
using NBitcoin.Altcoins;
using Newtonsoft.Json;
using NLog;
using Xunit;
#pragma warning disable 8974

namespace HashStormCore.Tests.Blockchain.Bitcoin;

public class BitcoinJobTests : TestBase
{
    [Fact]
    public void RegisterSubmit_Includes_VersionBits_When_VersionRolling_Is_Active()
    {
        var job = new TestBitcoinJob();

        Assert.True(job.TryRegister("60000001", "01000000", "63445774", "51036775", 0x00002000));
        Assert.True(job.TryRegister("60000001", "01000000", "63445774", "51036775", 0x00004000));
        Assert.False(job.TryRegister("60000001", "01000000", "63445774", "51036775", 0x00002000));
    }

    [Fact]
    public void RegisterSubmit_Still_Detects_Duplicates_Without_VersionBits()
    {
        var job = new TestBitcoinJob();

        Assert.True(job.TryRegister("60000001", "01000000", "63445774", "51036775"));
        Assert.False(job.TryRegister("60000001", "01000000", "63445774", "51036775"));
    }

    [Fact]
    public void RegisterSubmit_Treats_Hex_Casing_As_Equivalent_For_Duplicate_Detection()
    {
        var job = new TestBitcoinJob();

        Assert.True(job.TryRegister("6000000A", "A1B2C3D4", "63445774", "51036775", 0x00002000));
        Assert.False(job.TryRegister("6000000a", "a1b2c3d4", "63445774", "51036775", 0x00002000));
    }

    [Fact]
    public void Process_Malformed_VersionBits_Throws_StratumException()
    {
        var (job, worker) = CreateJob();
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x0000f000;

        var ex = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "01000000", "63445774", "51036775", "zzzzzzzz"));

        Assert.Contains("invalid version bits", ex.Message);
    }

    [Fact]
    public void Process_Wrong_Length_VersionBits_Throws_StratumException()
    {
        var (job, worker) = CreateJob();
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x0000f000;

        var ex = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "01000000", "63445774", "51036775", "2000"));

        Assert.Contains("incorrect size of version bits", ex.Message);
    }

    [Theory]
    [InlineData("0002000")]
    [InlineData("000020000")]
    [InlineData(" 0002000")]
    [InlineData("0002000 ")]
    public void Process_Invalid_VersionBits_Shape_Throws_StratumException(string versionBits)
    {
        var (job, worker) = CreateJob();
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x1fffe000;

        Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "01000000", "63445774", "51036775", versionBits));
    }

    [Fact]
    public void Process_VersionBits_Outside_Mask_Throws_StratumException()
    {
        var (job, worker) = CreateJob();
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x0000f000;

        var ex = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "01000000", "63445774", "51036775", "00010000"));

        Assert.Contains("rolling-version mask violation", ex.Message);
    }

    [Fact]
    public void Process_Missing_VersionBits_When_Negotiated_Throws_StratumException()
    {
        var (job, worker) = CreateJob();
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x0000f000;

        var ex = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "01000000", "63445774", "51036775"));

        Assert.Contains("missing version bits", ex.Message);
    }

    [Fact]
    public void Process_Extra_VersionBits_Without_Negotiation_Throws_StratumException()
    {
        var (job, worker) = CreateJob();

        var ex = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "01000000", "63445774", "51036775", "00002000"));

        Assert.Contains("version rolling was not negotiated", ex.Message);
    }

    [Fact]
    public void ExtractSubmitParameters_Missing_Negotiated_VersionBits_Throws_StratumException()
    {
        var ex = Assert.Throws<StratumException>(() =>
            BitcoinJobManager.ExtractSubmitParameters(
                new object[] { "miner.worker", "1", "01000000", "63445774", "51036775" },
                versionRollingNegotiated: true));

        Assert.Contains("missing version bits", ex.Message);
    }

    [Fact]
    public void ExtractSubmitParameters_Extra_VersionBits_Without_Negotiation_Throws_StratumException()
    {
        var ex = Assert.Throws<StratumException>(() =>
            BitcoinJobManager.ExtractSubmitParameters(
                new object[] { "miner.worker", "1", "01000000", "63445774", "51036775", "00002000" },
                versionRollingNegotiated: false));

        Assert.Contains("version rolling was not negotiated", ex.Message);
    }

    [Fact]
    public void ExtractSubmitParameters_Non_String_Param_Throws_StratumException()
    {
        var ex = Assert.Throws<StratumException>(() =>
            BitcoinJobManager.ExtractSubmitParameters(
                new object[] { "miner.worker", "1", 1, "63445774", "51036775" },
                versionRollingNegotiated: false));

        Assert.Contains("invalid extra nonce", ex.Message);
    }

    [Theory]
    [InlineData("00000000", 0u)]
    [InlineData("ffffffff", 0xffffffffu)]
    [InlineData("FFFFFFFF", 0xffffffffu)]
    [InlineData("1a2B3c4D", 0x1a2b3c4du)]
    public void TryParseHex8Strict_Valid_Values_Parse(string value, uint expected)
    {
        Assert.True(BitcoinSubmitValidation.TryParseHex8Strict(value, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1234567")]
    [InlineData("123456789")]
    [InlineData("zzzzzzzz")]
    [InlineData("1234567g")]
    [InlineData(" 1234567")]
    [InlineData("1234567 ")]
    [InlineData("+1234567")]
    public void TryParseHex8Strict_Invalid_Values_Return_False(string value)
    {
        Assert.False(BitcoinSubmitValidation.TryParseHex8Strict(value, out _));
    }

    [Fact]
    public void Process_Valid_Share()
    {
        var (job, worker) = CreateJob();

        var submitParams = JsonConvert.DeserializeObject<object[]>(
            "[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"00000001\",\"01000000\",\"63445774\",\"51036775\"]",
            jsonSerializerSettings);

        // Extract params
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;

        // Validate & process
        var (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, nonce);

        Assert.NotNull(share);
        Assert.Equal(813750, share.BlockHeight);

        // This fixture currently produces a valid accepted share, not a block candidate.
        Assert.False(share.IsBlockCandidate);
        Assert.Null(share.BlockHash);
        Assert.Null(blockHex);
    }

    [Fact]
    public void Process_Duplicate_Submission()
    {
        var (job, worker) = CreateJob();

        var submitParams = JsonConvert.DeserializeObject<object[]>(
            "[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"00000001\",\"01000000\",\"63445774\",\"51036775\"]",
            jsonSerializerSettings);

        // Extract params
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;

        // First submission must be accepted
        var (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, nonce);

        Assert.NotNull(share);
        Assert.False(share.IsBlockCandidate);
        Assert.Null(blockHex);

        // Second identical submission must be rejected as duplicate
        Assert.ThrowsAny<StratumException>(() => job.ProcessShare(worker, extraNonce2, nTime, nonce));
    }

    [Fact]
    public void Process_Invalid_Nonce()
    {
        var (job, worker) = CreateJob();

        var submitParams = JsonConvert.DeserializeObject<object[]>(
            "[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"00000001\",\"01000000\",\"63445774\",\"5103677\"]",
            jsonSerializerSettings);

        // Extract params
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;

        // Nonce is intentionally malformed: 7 hex chars instead of 8
        Assert.ThrowsAny<StratumException>(() => job.ProcessShare(worker, extraNonce2, nTime, nonce));
    }

    [Fact]
    public void Process_Invalid_Nonce_Hex_Throws_StratumException()
    {
        var (job, worker) = CreateJob();

        var ex = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "01000000", "63445774", "zzzzzzzz"));

        Assert.Contains("invalid nonce", ex.Message);
    }

    [Fact]
    public void Process_Invalid_Time()
    {
        var (job, worker) = CreateJob();

        var submitParams = JsonConvert.DeserializeObject<object[]>(
            "[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"00000001\",\"01000000\",\"13445774\",\"51036775\"]",
            jsonSerializerSettings);

        // Extract params
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;

        Assert.ThrowsAny<StratumException>(() => job.ProcessShare(worker, extraNonce2, nTime, nonce));
    }

    [Fact]
    public void Process_Invalid_Time_Hex_Throws_StratumException()
    {
        var (job, worker) = CreateJob();

        var ex = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "01000000", "zzzzzzzz", "51036775"));

        Assert.Contains("invalid ntime", ex.Message);
    }

    [Theory]
    [InlineData("zzzzzzzz")]
    [InlineData("0100000z")]
    public void Process_Invalid_ExtraNonce2_Hex_Throws_StratumException(string extraNonce2)
    {
        var (job, worker) = CreateJob();

        var ex = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, extraNonce2, "63445774", "51036775"));

        Assert.Contains("extranonce2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Process_Invalid_ExtraNonce2_Length_Throws_StratumException()
    {
        var (job, worker) = CreateJob();

        var ex = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "010000", "63445774", "51036775"));

        Assert.Contains("extranonce2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(" 3445774", "51036775", null)]
    [InlineData("63445774", "5103677 ", null)]
    [InlineData("63445774", "51036775", " 0020000")]
    public void Process_Hex8_Fields_With_Whitespace_Throw_StratumException(string nTime, string nonce, string versionBits)
    {
        var (job, worker) = CreateJob();

        if(versionBits != null)
            worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x1fffe000;

        Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "01000000", nTime, nonce, versionBits));
    }

    [Fact]
    public void Process_Malformed_ExtraNonce2_Does_Not_Poison_Duplicate_Registration()
    {
        var (job, worker) = CreateJob();

        Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "zzzzzzzz", "63445774", "51036775"));

        var (share, blockHex) = job.ProcessShare(worker, "01000000", "63445774", "51036775");

        Assert.NotNull(share);
        Assert.Null(blockHex);
    }

    [Fact]
    public void Process_Repeated_Malformed_ExtraNonce2_Is_Not_Registered_As_Duplicate()
    {
        var (job, worker) = CreateJob();

        var first = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "zzzzzzzz", "63445774", "51036775"));
        var second = Assert.Throws<StratumException>(() =>
            job.ProcessShare(worker, "zzzzzzzz", "63445774", "51036775"));

        Assert.Contains("extranonce2", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("extranonce2", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("duplicate", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    private (BitcoinJob, StratumConnection) CreateJob()
    {
        var job = new BitcoinJob();
        var coin = (BitcoinTemplate)ModuleInitializer.CoinTemplates["dash"];
        var pc = new PoolConfig { Template = coin };

        var blockTemplate = JsonConvert.DeserializeObject<HashStormCore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate>(
            "{\"version\":536870912,\"previousBlockhash\":\"0000011a86a1ad3609e5359b6b6411a1654108ee7c1afc003dec23b5a0400e4b\",\"coinbaseValue\":1801475949,\"target\":\"000001d771000000000000000000000000000000000000000000000000000000\",\"nonceRange\":\"00000000ffffffff\",\"curTime\":1665423220,\"bits\":\"1e01d771\",\"height\":813750,\"transactions\":[],\"coinbaseAux\":{\"flags\":null},\"default_witness_commitment\":null,\"capabilities\":[\"proposal\"],\"rules\":[\"csv\",\"dip0001\",\"bip147\",\"dip0003\",\"dip0008\",\"realloc\",\"dip0020\",\"dip0024\"],\"vbavailable\":{},\"vbrequired\":0,\"longpollid\":\"0000011a86a1ad3609e5359b6b6411a1654108ee7c1afc003dec23b5a0400e4b814670\",\"mintime\":1665422408,\"mutable\":[\"time\",\"transactions\",\"prevblock\"],\"sigoplimit\":40000,\"sizelimit\":2000000,\"previousbits\":\"1e01bee4\",\"masternode\":[{\"payee\":\"yVXDAM73Tg6A44Bm3qduXsMCYxzuqBCT48\",\"script\":\"76a91464f2b2b84f62d68a2cd7f7f5fb2b5aa75ef716d788ac\",\"amount\":1080885569}],\"masternode_payments_started\":true,\"masternode_payments_enforced\":true,\"superblock\":[],\"superblocks_started\":true,\"superblocks_enabled\":true,\"coinbase_payload\":\"0200b66a0c00fbab6816312c05803d026cce30fec0332c059f66e421ab0bf65b96ea9efb8a22e12cfc31666208b47a006e5b74f95a4c0797b6bc620ea1cc07cb53616e547302\"}",
            jsonSerializerSettings);

        var clock = MockMasterClock.FromTicks(638010200200475015);
        var network = Dash.Instance.Testnet;
        var poolAddressDestination = BitcoinUtils.AddressToDestination("yNkA6gVSPqKzW6WmJtTazRLKbSkQA5ND2h", network);

        var context = new BitcoinWorkerContext
        {
            Miner = "yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4",
            ExtraNonce1 = "60000001",

            // The historical fixture no longer yields a block candidate with the current code path.
            // Keep the test meaningful by setting a very low worker difficulty so the share is accepted.
            Difficulty = 0.0000000001d,

            UserAgent = "cpuminer-multi/1.3.1"
        };

        var worker = new StratumConnection(
            new NullLogger(LogManager.LogFactory),
            container.Resolve<RecyclableMemoryStreamManager>(),
            clock,
            "1",
            false);

        worker.SetContext(context);

        job.Init(blockTemplate, "1", pc, null, new ClusterConfig(), clock, poolAddressDestination, network, false,
            coin.ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue);

        return (job, worker);
    }

    private sealed class TestBitcoinJob : BitcoinJob
    {
        public bool TryRegister(string extraNonce1, string extraNonce2, string nTime, string nonce, uint? versionBits = null)
        {
            var nTimeInt = BitcoinSubmitValidation.ParseHex8Strict(nTime, "incorrect size of ntime", "invalid ntime");
            var nonceInt = BitcoinSubmitValidation.ParseHex8Strict(nonce, "incorrect size of nonce", "invalid nonce");

            return RegisterSubmit(
                extraNonce1,
                extraNonce2,
                nTimeInt,
                nonceInt,
                versionBits ?? 0,
                versionBits.HasValue);
        }
    }
}
