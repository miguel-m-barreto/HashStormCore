using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using HashStormCore.Blockchain.Bitcoin;

namespace HashStormCore.Benchmarks.MiningSubmit;

[MemoryDiagnoser]
[MinIterationTime(250)]
[CategoriesColumn]
[BenchmarkCategory("MiningSubmit", "Bitcoin", "DuplicateRegister", "Production")]
public class BitcoinSubmitDuplicateRegisterBenchmarks
{
    private const int MediumSetSize = 1000;
    private const int LargeSetSize = 100000;

    private readonly ConcurrentDictionary<BitcoinSubmitDuplicateKey, bool> singleKeySubmissions = new();
    private readonly ConcurrentDictionary<BitcoinSubmitDuplicateKey, bool> mediumSubmissions = new();
    private readonly ConcurrentDictionary<BitcoinSubmitDuplicateKey, bool> largeSubmissions = new();
    private readonly ConcurrentDictionary<BitcoinSubmitDuplicateKey, bool> batchSubmissions = new();
    private readonly ConcurrentDictionary<BitcoinSubmitDuplicateKey, bool> differentVersionBitsSubmissions = new();
    private readonly ConcurrentDictionary<string, bool> stringKeySubmissions = new(StringComparer.Ordinal);

    private int keyIndex;

    private BitcoinSubmitDuplicateKey repeatedKey;
    private BitcoinSubmitDuplicateKey legacyKey;
    private BitcoinSubmitDuplicateKey versionRollingKey;
    private BitcoinSubmitDuplicateKey sameTupleVersionBits0;
    private BitcoinSubmitDuplicateKey sameTupleVersionBits1;
    private BitcoinSubmitDuplicateKey[] uniqueKeys = null!;
    private BitcoinSubmitDuplicateKey[] differentVersionBitsKeys = null!;
    private BitcoinSubmitDuplicateKey[] manyExtraNonce2Keys = null!;
    private BitcoinSubmitDuplicateKey[] manyNonceKeys = null!;
    private string[] uniqueExtraNonce2Values = null!;
    private uint[] uniqueNonceValues = null!;
    private string[] stringEquivalentKeys = null!;

    [GlobalSetup]
    public void Setup()
    {
        uniqueKeys = new BitcoinSubmitDuplicateKey[LargeSetSize];
        differentVersionBitsKeys = new BitcoinSubmitDuplicateKey[MediumSetSize];
        manyExtraNonce2Keys = new BitcoinSubmitDuplicateKey[MediumSetSize];
        manyNonceKeys = new BitcoinSubmitDuplicateKey[MediumSetSize];
        uniqueExtraNonce2Values = new string[MediumSetSize];
        uniqueNonceValues = new uint[MediumSetSize];
        stringEquivalentKeys = new string[MediumSetSize];

        repeatedKey = BitcoinSubmitValidation.CreateDuplicateKey("f00dbabe", "01000000", 0x65f1a2b3, 0x5103677a, 0x20000000, true);
        legacyKey = BitcoinSubmitValidation.CreateDuplicateKey("f00dbabe", "01000000", 0x65f1a2b3, 0x5103677a, 0, false);
        versionRollingKey = repeatedKey;
        sameTupleVersionBits0 = BitcoinSubmitValidation.CreateDuplicateKey("f00dbabe", "01000000", 0x65f1a2b3, 0x5103677a, 0x20000000, true);
        sameTupleVersionBits1 = BitcoinSubmitValidation.CreateDuplicateKey("f00dbabe", "01000000", 0x65f1a2b3, 0x5103677a, 0x20000001, true);

        for(var i = 0; i < uniqueKeys.Length; i++)
        {
            var extraNonce2 = ((uint) i).ToString("x8");
            var nonce = 0x51000000u + (uint) i;

            uniqueKeys[i] = BitcoinSubmitValidation.CreateDuplicateKey(
                "f00dbabe",
                extraNonce2,
                0x65f1a2b3,
                nonce,
                0x20000000,
                true);

            if(i < MediumSetSize)
            {
                uniqueExtraNonce2Values[i] = extraNonce2;
                uniqueNonceValues[i] = nonce;

                differentVersionBitsKeys[i] = BitcoinSubmitValidation.CreateDuplicateKey(
                    "f00dbabe",
                    "01000000",
                    0x65f1a2b3,
                    0x5103677a,
                    0x20000000u + (uint) i,
                    true);

                manyExtraNonce2Keys[i] = BitcoinSubmitValidation.CreateDuplicateKey(
                    "f00dbabe",
                    extraNonce2,
                    0x65f1a2b3,
                    0x5103677a,
                    0x20000000,
                    true);

                manyNonceKeys[i] = BitcoinSubmitValidation.CreateDuplicateKey(
                    "f00dbabe",
                    "01000000",
                    0x65f1a2b3,
                    nonce,
                    0x20000000,
                    true);

                stringEquivalentKeys[i] = string.Join(':',
                    "f00dbabe",
                    extraNonce2,
                    0x65f1a2b3u.ToString("x8"),
                    nonce.ToString("x8"),
                    0x20000000u.ToString("x8"));
            }
        }

        ResetPrepopulatedDictionaries();
    }

    [Benchmark]
    [BenchmarkCategory("HitPath", "SetSize1")]
    public bool Production_DuplicateRegister_TryAdd_DuplicateSubmit_SetSize1()
    {
        return singleKeySubmissions.TryAdd(repeatedKey, true);
    }

    [Benchmark]
    [BenchmarkCategory("HitPath", "SetSize1000")]
    public bool Production_DuplicateRegister_TryAdd_DuplicateSubmit_SetSize1000()
    {
        return mediumSubmissions.TryAdd(Next(uniqueKeys, MediumSetSize), true);
    }

    [Benchmark]
    [BenchmarkCategory("HitPath", "SetSize100000")]
    public bool Production_DuplicateRegister_TryAdd_DuplicateSubmit_SetSize100000()
    {
        return largeSubmissions.TryAdd(Next(uniqueKeys, LargeSetSize), true);
    }

    [Benchmark]
    [BenchmarkCategory("HitPath")]
    public bool Production_DuplicateRegister_TryAdd_DuplicateSubmit()
    {
        return mediumSubmissions.TryAdd(repeatedKey, true);
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("MissPath", "Batch")]
    public int Production_DuplicateRegister_TryAdd_NewSubmit()
    {
        batchSubmissions.Clear();
        var added = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            if(batchSubmissions.TryAdd(uniqueKeys[i], true))
                added++;
        }

        return added;
    }

    [Benchmark]
    [BenchmarkCategory("HitPath")]
    public bool Production_DuplicateRegister_TryAdd_RepeatedSameKey()
    {
        return singleKeySubmissions.TryAdd(repeatedKey, true);
    }

    [Benchmark]
    [BenchmarkCategory("VersionRolling", "ResetPerOperation")]
    public bool Production_DuplicateRegister_TryAdd_DifferentVersionBits_ResetPerOperation_TwoInserts()
    {
        differentVersionBitsSubmissions.Clear();
        differentVersionBitsSubmissions.TryAdd(sameTupleVersionBits0, true);

        return differentVersionBitsSubmissions.TryAdd(sameTupleVersionBits1, true);
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("VersionRolling", "Batch")]
    public int Production_DuplicateRegister_TryAdd_DifferentVersionBits()
    {
        batchSubmissions.Clear();
        var added = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            if(batchSubmissions.TryAdd(differentVersionBitsKeys[i], true))
                added++;
        }

        return added;
    }

    [Benchmark]
    [BenchmarkCategory("Legacy")]
    public bool Production_DuplicateRegister_TryAdd_LegacyNoVersionRolling()
    {
        return mediumSubmissions.TryAdd(legacyKey, true);
    }

    [Benchmark]
    [BenchmarkCategory("VersionRolling")]
    public bool Production_DuplicateRegister_TryAdd_VersionRolling()
    {
        return mediumSubmissions.TryAdd(versionRollingKey, true);
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("MissPath", "Batch", "Nonce")]
    public int Production_DuplicateRegister_TryAdd_ManyUnique()
    {
        batchSubmissions.Clear();
        var added = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            if(batchSubmissions.TryAdd(uniqueKeys[i], true))
                added++;
        }

        return added;
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("HitPath", "Batch")]
    public int Production_DuplicateRegister_TryAdd_RepeatedSameKey_Batch()
    {
        batchSubmissions.Clear();
        batchSubmissions.TryAdd(repeatedKey, true);
        var duplicates = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            if(!batchSubmissions.TryAdd(repeatedKey, true))
                duplicates++;
        }

        return duplicates;
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("MissPath", "Batch", "ExtraNonce2")]
    public int Production_DuplicateRegister_TryAdd_ManyExtraNonce2Values()
    {
        batchSubmissions.Clear();
        var added = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            if(batchSubmissions.TryAdd(manyExtraNonce2Keys[i], true))
                added++;
        }

        return added;
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("MissPath", "Batch", "Nonce")]
    public int Production_DuplicateRegister_TryAdd_ManyNonceValues()
    {
        batchSubmissions.Clear();
        var added = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            if(batchSubmissions.TryAdd(manyNonceKeys[i], true))
                added++;
        }

        return added;
    }

    [Benchmark]
    [BenchmarkCategory("HitPath", "ConstructAndTryAdd")]
    public bool Production_DuplicateRegister_ConstructAndTryAdd_DuplicateSubmit()
    {
        var key = BitcoinSubmitValidation.CreateDuplicateKey("f00dbabe", "01000000", 0x65f1a2b3, 0x5103677a, 0x20000000, true);
        return singleKeySubmissions.TryAdd(key, true);
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("MissPath", "ConstructAndTryAdd", "Batch")]
    public int Production_DuplicateRegister_ConstructAndTryAdd_NewSubmit()
    {
        batchSubmissions.Clear();
        var added = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            var key = BitcoinSubmitValidation.CreateDuplicateKey(
                "f00dbabe",
                uniqueExtraNonce2Values[i],
                0x65f1a2b3,
                uniqueNonceValues[i],
                0x20000000,
                true);

            if(batchSubmissions.TryAdd(key, true))
                added++;
        }

        return added;
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("StandaloneEquivalent", "MissPath", "Batch")]
    public int ApplesToApples_DuplicateRegister_TryAdd_StringKeyEquivalent()
    {
        stringKeySubmissions.Clear();
        var added = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            if(stringKeySubmissions.TryAdd(stringEquivalentKeys[i], true))
                added++;
        }

        return added;
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("Production", "MissPath", "Batch")]
    public int ApplesToApples_DuplicateRegister_TryAdd_Production()
    {
        batchSubmissions.Clear();
        var added = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            if(batchSubmissions.TryAdd(manyExtraNonce2Keys[i], true))
                added++;
        }

        return added;
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("Production", "MissPath", "ConstructAndTryAdd", "Batch")]
    public int ApplesToApples_DuplicateRegister_ConstructAndTryAdd_Production()
    {
        batchSubmissions.Clear();
        var added = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            var key = BitcoinSubmitValidation.CreateDuplicateKey(
                "f00dbabe",
                uniqueExtraNonce2Values[i],
                0x65f1a2b3,
                uniqueNonceValues[i],
                0x20000000,
                true);

            if(batchSubmissions.TryAdd(key, true))
                added++;
        }

        return added;
    }

    [Benchmark(OperationsPerInvoke = MediumSetSize)]
    [BenchmarkCategory("StandaloneEquivalent", "MissPath", "ConstructAndTryAdd", "Batch")]
    public int ApplesToApples_DuplicateRegister_ConstructAndTryAdd_StringKeyEquivalent()
    {
        stringKeySubmissions.Clear();
        var added = 0;

        for(var i = 0; i < MediumSetSize; i++)
        {
            var key = BuildStringKey(
                "f00dbabe",
                uniqueExtraNonce2Values[i],
                0x65f1a2b3,
                uniqueNonceValues[i],
                0x20000000);

            if(stringKeySubmissions.TryAdd(key, true))
                added++;
        }

        return added;
    }

    private void ResetPrepopulatedDictionaries()
    {
        singleKeySubmissions.Clear();
        mediumSubmissions.Clear();
        largeSubmissions.Clear();

        singleKeySubmissions.TryAdd(repeatedKey, true);

        for(var i = 0; i < MediumSetSize; i++)
            mediumSubmissions.TryAdd(uniqueKeys[i], true);

        mediumSubmissions.TryAdd(repeatedKey, true);
        mediumSubmissions.TryAdd(legacyKey, true);
        mediumSubmissions.TryAdd(versionRollingKey, true);

        for(var i = 0; i < LargeSetSize; i++)
            largeSubmissions.TryAdd(uniqueKeys[i], true);
    }

    private BitcoinSubmitDuplicateKey Next(BitcoinSubmitDuplicateKey[] values, int length)
    {
        var current = unchecked(keyIndex++);
        return values[(int) ((uint) current % (uint) length)];
    }

    private static string BuildStringKey(string extraNonce1, string extraNonce2, uint nTime, uint nonce, uint versionBits)
    {
        return string.Join(':', extraNonce1, extraNonce2, nTime.ToString("x8"), nonce.ToString("x8"), versionBits.ToString("x8"));
    }
}
