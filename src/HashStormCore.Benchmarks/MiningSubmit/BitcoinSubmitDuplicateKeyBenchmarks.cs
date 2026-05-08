using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using HashStormCore.Blockchain.Bitcoin;

namespace HashStormCore.Benchmarks.MiningSubmit;

[MemoryDiagnoser]
[MinIterationTime(250)]
[CategoriesColumn]
[BenchmarkCategory("MiningSubmit", "Bitcoin", "DuplicateKey")]
public class BitcoinSubmitDuplicateKeyBenchmarks
{
    private readonly Consumer consumer = new();
    private readonly HashSet<string> stringKeySet = new(StringComparer.Ordinal);
    private readonly HashSet<BitcoinSubmitDuplicateKey> productionKeySet = new();
    private readonly HashSet<PrototypeDuplicateKey> prototypeKeySet = new();
    private readonly ConcurrentDictionary<string, bool> stringRegister = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<BitcoinSubmitDuplicateKey, bool> productionRegister = new();

    private int index;

    private string[] extraNonce1Values = null!;
    private string[] extraNonce2Values = null!;
    private uint[] nTimeValues = null!;
    private uint[] nonceValues = null!;
    private uint[] versionBitsValues = null!;
    private string[] stringKeys = null!;
    private BitcoinSubmitDuplicateKey[] productionKeys = null!;
    private PrototypeDuplicateKey[] prototypeKeys = null!;

    private BitcoinSubmitDuplicateKey productionSameKey;
    private BitcoinSubmitDuplicateKey productionDifferentNonce;
    private BitcoinSubmitDuplicateKey productionDifferentVersionBits;
    private BitcoinSubmitDuplicateKey productionDifferentExtraNonce2;
    private BitcoinSubmitDuplicateKey productionDifferentExtraNonce1;
    private BitcoinSubmitDuplicateKey productionLegacyA;
    private BitcoinSubmitDuplicateKey productionLegacyB;
    private PrototypeDuplicateKey prototypeSameKey;
    private PrototypeDuplicateKey prototypeDifferentVersionBits;

    [GlobalSetup]
    public void Setup()
    {
        extraNonce1Values = ["f00dbabe", "cafefeed", "00112233", "89abcdef"];
        extraNonce2Values = ["01000000", "02000000", "a1b2c3d4", "feedbeef"];
        nTimeValues = [0x65f1a2b3, 0x65f1a2b4, 0x65f1a2b5, 0x65f1a2b6];
        nonceValues = [0x5103677a, 0x5103677b, 0x5103677c, 0x5103677d];
        versionBitsValues = [0x20000000, 0x20000001, 0x20000002, 0x20000003];

        stringKeys = new string[extraNonce1Values.Length];
        productionKeys = new BitcoinSubmitDuplicateKey[extraNonce1Values.Length];
        prototypeKeys = new PrototypeDuplicateKey[extraNonce1Values.Length];

        stringKeySet.Clear();
        productionKeySet.Clear();
        prototypeKeySet.Clear();

        for(var i = 0; i < extraNonce1Values.Length; i++)
        {
            stringKeys[i] = BuildStringKey(extraNonce1Values[i], extraNonce2Values[i], nTimeValues[i], nonceValues[i], versionBitsValues[i]);
            productionKeys[i] = BitcoinSubmitValidation.CreateDuplicateKey(
                extraNonce1Values[i],
                extraNonce2Values[i],
                nTimeValues[i],
                nonceValues[i],
                versionBitsValues[i],
                true);
            prototypeKeys[i] = new PrototypeDuplicateKey(
                extraNonce1Values[i],
                extraNonce2Values[i],
                nTimeValues[i],
                nonceValues[i],
                versionBitsValues[i]);

            stringKeySet.Add(stringKeys[i]);
            productionKeySet.Add(productionKeys[i]);
            prototypeKeySet.Add(prototypeKeys[i]);
            stringRegister.TryAdd(stringKeys[i], true);
            productionRegister.TryAdd(productionKeys[i], true);
        }

        productionSameKey = productionKeys[0];
        productionDifferentNonce = BitcoinSubmitValidation.CreateDuplicateKey("f00dbabe", "01000000", 0x65f1a2b3, 0x5103677b, 0x20000000, true);
        productionDifferentVersionBits = BitcoinSubmitValidation.CreateDuplicateKey("f00dbabe", "01000000", 0x65f1a2b3, 0x5103677a, 0x20000001, true);
        productionDifferentExtraNonce2 = BitcoinSubmitValidation.CreateDuplicateKey("f00dbabe", "02000000", 0x65f1a2b3, 0x5103677a, 0x20000000, true);
        productionDifferentExtraNonce1 = BitcoinSubmitValidation.CreateDuplicateKey("cafefeed", "01000000", 0x65f1a2b3, 0x5103677a, 0x20000000, true);
        productionLegacyA = BitcoinSubmitValidation.CreateDuplicateKey("f00dbabe", "01000000", 0x65f1a2b3, 0x5103677a, 0, false);
        productionLegacyB = BitcoinSubmitValidation.CreateDuplicateKey("f00dbabe", "01000000", 0x65f1a2b3, 0x5103677a, 0xffffffff, false);
        prototypeSameKey = prototypeKeys[0];
        prototypeDifferentVersionBits = new PrototypeDuplicateKey("f00dbabe", "01000000", 0x65f1a2b3, 0x5103677a, 0x20000001);

        if(!productionLegacyA.Equals(productionLegacyB))
            throw new InvalidOperationException("Legacy production duplicate-key semantics are inconsistent");
    }

    [Benchmark]
    [BenchmarkCategory("Production", "Hash")]
    public int Production_DuplicateKey_GetHashCode()
    {
        return Next(productionKeys).GetHashCode();
    }

    [Benchmark]
    [BenchmarkCategory("Production", "Equality")]
    public bool Production_DuplicateKey_Equals_SameKey()
    {
        return productionKeys[0].Equals(productionSameKey);
    }

    [Benchmark]
    [BenchmarkCategory("Production", "Equality")]
    public bool Production_DuplicateKey_Equals_DifferentNonce()
    {
        return productionKeys[0].Equals(productionDifferentNonce);
    }

    [Benchmark]
    [BenchmarkCategory("Production", "Equality")]
    public bool Production_DuplicateKey_Equals_DifferentVersionBits()
    {
        return productionKeys[0].Equals(productionDifferentVersionBits);
    }

    [Benchmark]
    [BenchmarkCategory("Production", "Equality")]
    public bool Production_DuplicateKey_Equals_DifferentExtraNonce2()
    {
        return productionKeys[0].Equals(productionDifferentExtraNonce2);
    }

    [Benchmark]
    [BenchmarkCategory("Production", "Equality")]
    public bool Production_DuplicateKey_Equals_DifferentExtraNonce1()
    {
        return productionKeys[0].Equals(productionDifferentExtraNonce1);
    }

    [Benchmark]
    [BenchmarkCategory("Production", "Equality")]
    public bool Production_DuplicateKey_Equals_LegacyNoVersionBitsEquivalent()
    {
        return productionLegacyA.Equals(productionLegacyB);
    }

    [Benchmark]
    [BenchmarkCategory("Production", "Equality")]
    public bool Production_DuplicateKey_Equals_VersionRollingDifferentBits()
    {
        return !productionKeys[0].Equals(productionDifferentVersionBits);
    }

    [Benchmark]
    [BenchmarkCategory("ApplesToApples", "Production")]
    public void ApplesToApples_DuplicateKey_Construct_Production()
    {
        var i = NextIndex();
        consumer.Consume(BitcoinSubmitValidation.CreateDuplicateKey(
            extraNonce1Values[i],
            extraNonce2Values[i],
            nTimeValues[i],
            nonceValues[i],
            versionBitsValues[i],
            true));
    }

    [Benchmark]
    [BenchmarkCategory("ApplesToApples", "Prototype")]
    public void ApplesToApples_DuplicateKey_Construct_Prototype()
    {
        var i = NextIndex();
        consumer.Consume(new PrototypeDuplicateKey(
            extraNonce1Values[i],
            extraNonce2Values[i],
            nTimeValues[i],
            nonceValues[i],
            versionBitsValues[i]));
    }

    [Benchmark]
    [BenchmarkCategory("ApplesToApples", "Production")]
    public bool ApplesToApples_DuplicateKey_Lookup_Production()
    {
        return productionKeySet.Contains(Next(productionKeys));
    }

    [Benchmark]
    [BenchmarkCategory("ApplesToApples", "Prototype")]
    public bool ApplesToApples_DuplicateKey_Lookup_Prototype()
    {
        return prototypeKeySet.Contains(Next(prototypeKeys));
    }

    [Benchmark]
    [BenchmarkCategory("ApplesToApples", "Production")]
    public bool ApplesToApples_DuplicateKey_ConstructAndLookup_Production()
    {
        var i = NextIndex();
        var key = BitcoinSubmitValidation.CreateDuplicateKey(
            extraNonce1Values[i],
            extraNonce2Values[i],
            nTimeValues[i],
            nonceValues[i],
            versionBitsValues[i],
            true);

        return productionKeySet.Contains(key);
    }

    [Benchmark]
    [BenchmarkCategory("ApplesToApples", "Prototype")]
    public bool ApplesToApples_DuplicateKey_ConstructAndLookup_Prototype()
    {
        var i = NextIndex();
        var key = new PrototypeDuplicateKey(
            extraNonce1Values[i],
            extraNonce2Values[i],
            nTimeValues[i],
            nonceValues[i],
            versionBitsValues[i]);

        return prototypeKeySet.Contains(key);
    }

    [Benchmark]
    [BenchmarkCategory("ApplesToApples", "Production", "DuplicateRegister")]
    public bool ApplesToApples_DuplicateRegister_TryAdd_Production()
    {
        var i = NextIndex();
        return productionRegister.TryAdd(productionKeys[i], true);
    }

    [Benchmark]
    [BenchmarkCategory("ApplesToApples", "StandaloneEquivalent", "DuplicateRegister")]
    public bool ApplesToApples_DuplicateRegister_TryAdd_StringKeyEquivalent()
    {
        var i = NextIndex();
        return stringRegister.TryAdd(stringKeys[i], true);
    }

    [Benchmark]
    [BenchmarkCategory("Prototype", "Hash")]
    public int Prototype_DuplicateKey_GetHashCode()
    {
        return Next(prototypeKeys).GetHashCode();
    }

    [Benchmark]
    [BenchmarkCategory("Prototype", "Equality")]
    public bool Prototype_DuplicateKey_Equals_SameKey()
    {
        return prototypeKeys[0].Equals(prototypeSameKey);
    }

    [Benchmark]
    [BenchmarkCategory("Prototype", "Equality")]
    public bool Prototype_DuplicateKey_Equals_DifferentVersionBits()
    {
        return !prototypeKeys[0].Equals(prototypeDifferentVersionBits);
    }

    private BitcoinSubmitDuplicateKey Next(BitcoinSubmitDuplicateKey[] values)
    {
        return values[NextIndex()];
    }

    private PrototypeDuplicateKey Next(PrototypeDuplicateKey[] values)
    {
        return values[NextIndex()];
    }

    private int NextIndex()
    {
        var current = unchecked(index++);
        return (int) ((uint) current % (uint) extraNonce1Values.Length);
    }

    private static string BuildStringKey(string extraNonce1, string extraNonce2, uint nTime, uint nonce, uint versionBits)
    {
        return string.Join(':', extraNonce1, extraNonce2, nTime.ToString("x8"), nonce.ToString("x8"), versionBits.ToString("x8"));
    }

    public readonly record struct PrototypeDuplicateKey(
        string ExtraNonce1,
        string ExtraNonce2,
        uint NTime,
        uint Nonce,
        uint? VersionBits);
}
