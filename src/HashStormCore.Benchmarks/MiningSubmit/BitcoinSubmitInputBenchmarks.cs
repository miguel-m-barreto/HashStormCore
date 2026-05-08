using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using HashStormCore.Blockchain.Bitcoin;
using HashStormCore.Stratum;

namespace HashStormCore.Benchmarks.MiningSubmit;

[MemoryDiagnoser]
[MinIterationTime(250)]
[CategoriesColumn]
[BenchmarkCategory("MiningSubmit", "Bitcoin", "InputValidation")]
// Bitcoin submit input-validation hot-path benchmarks. Production_* methods call
// the extracted production helpers; StandaloneEquivalent_* and Prototype_* methods
// remain benchmark-only comparisons.
public class BitcoinSubmitInputBenchmarks
{
    private const int RegistrationBatchSize = 1000;

    private readonly HashSet<string> duplicateSubmits = new(StringComparer.Ordinal);
    private readonly HashSet<BitcoinSubmitDuplicateKey> productionDuplicateSubmits = new();
    private readonly ConcurrentDictionary<BitcoinSubmitDuplicateKey, bool> submitRegistration = new();
    private readonly ConcurrentDictionary<BitcoinSubmitDuplicateKey, bool> batchSubmitRegistration = new();
    private readonly Consumer consumer = new();

    private int expectedExtraNonce2Length;
    private int keyIndex;
    private int shapeIndex;

    private string[] extraNonce1Values = null!;
    private string[] validExtraNonce2Values = null!;
    private string[] validExtraNonce2UppercaseValues = null!;
    private string[] validExtraNonce2MixedCaseValues = null!;
    private string[] invalidExtraNonce2Values = null!;
    private string[] invalidExtraNonce2NonHexValues = null!;
    private string[] invalidExtraNonce2LengthValues = null!;
    private string[] validHex8Values = null!;
    private string[] invalidHex8Values = null!;
    private string[] nTimeValues = null!;
    private string[] nonceValues = null!;
    private string[] versionBitsValues = null!;
    private string[] batchExtraNonce2Values = null!;
    private string[] batchNonceValues = null!;
    private string[] validExtraNonce2LowercaseValues = null!;
    private uint[] parsedNTimeValues = null!;
    private uint[] parsedNonceValues = null!;
    private uint[] parsedVersionBitsValues = null!;
    private string[] prebuiltDuplicateKeys = null!;
    private BitcoinSubmitDuplicateKey[] prebuiltProductionDuplicateKeys = null!;
    private StructuredDuplicateKey[] prebuiltStructuredDuplicateKeys = null!;
    private CustomStructuredDuplicateKey[] prebuiltCustomStructuredDuplicateKeys = null!;
    private object[][] validLegacySubmitParams = null!;
    private object[][] validVersionRollingSubmitParams = null!;
    private object[][] invalidSubmitParams = null!;
    private readonly HashSet<StructuredDuplicateKey> structuredDuplicateSubmits = new();
    private readonly HashSet<CustomStructuredDuplicateKey> customStructuredDuplicateSubmits = new();

    [GlobalSetup]
    public void Setup()
    {
        expectedExtraNonce2Length = 8;
        extraNonce1Values = ["f00dbabe", "cafefeed", "00112233", "89abcdef"];
        validExtraNonce2Values = ["01000000", "02000000", "a1b2c3d4", "FEEDBEEF"];
        validExtraNonce2UppercaseValues = ["A1B2C3D4", "FEEDBEEF", "ABCDEF01", "FACEB00C"];
        validExtraNonce2MixedCaseValues = ["A1b2C3d4", "FeeDbEeF", "AbCdEf01", "fAcEb00C"];
        invalidExtraNonce2Values = ["zzzzzzzz", "0100000", "010000000", "0100000 "];
        invalidExtraNonce2NonHexValues = ["zzzzzzzz", "0100000g", "abcdez01", "0000000x"];
        invalidExtraNonce2LengthValues = ["0100000", "010000000", "", "0100000 "];
        validHex8Values = ["65f1a2b3", "5103677a", "20000000", "00000001"];
        invalidHex8Values = ["65f1a2b ", "5103677z", " 0002000", "abcdef0"];
        nTimeValues = ["65f1a2b3", "65f1a2b4", "65f1a2b5", "65f1a2b6"];
        nonceValues = ["5103677a", "5103677b", "5103677c", "5103677d"];
        versionBitsValues = ["20000000", "20000001", "20000002", "20000003"];
        batchExtraNonce2Values = new string[RegistrationBatchSize];
        batchNonceValues = new string[RegistrationBatchSize];
        validExtraNonce2LowercaseValues = new string[validExtraNonce2Values.Length];
        parsedNTimeValues = new uint[nTimeValues.Length];
        parsedNonceValues = new uint[nonceValues.Length];
        parsedVersionBitsValues = new uint[versionBitsValues.Length];
        validLegacySubmitParams =
        [
            ["worker", "job1", "01000000", "65f1a2b3", "5103677a"],
            ["worker", "job2", "02000000", "65f1a2b4", "5103677b"],
        ];
        validVersionRollingSubmitParams =
        [
            ["worker", "job1", "01000000", "65f1a2b3", "5103677a", "20000000"],
            ["worker", "job2", "02000000", "65f1a2b4", "5103677b", "20000001"],
        ];
        invalidSubmitParams =
        [
            ["worker", "job1", "01000000", "65f1a2b3", "5103677a", "20000000"],
            ["worker", "job1", "01000000", "65f1a2b3"],
            ["worker", "job1", 1, "65f1a2b3", "5103677a"],
        ];

        duplicateSubmits.Clear();
        productionDuplicateSubmits.Clear();
        structuredDuplicateSubmits.Clear();
        customStructuredDuplicateSubmits.Clear();

        for(var i = 0; i < RegistrationBatchSize; i++)
        {
            batchExtraNonce2Values[i] = ((uint) i).ToString("x8");
            batchNonceValues[i] = (0x51000000u + (uint) i).ToString("x8");
        }

        prebuiltDuplicateKeys = new string[validExtraNonce2Values.Length];
        prebuiltProductionDuplicateKeys = new BitcoinSubmitDuplicateKey[validExtraNonce2Values.Length];
        prebuiltStructuredDuplicateKeys = new StructuredDuplicateKey[validExtraNonce2Values.Length];
        prebuiltCustomStructuredDuplicateKeys = new CustomStructuredDuplicateKey[validExtraNonce2Values.Length];
        for(var i = 0; i < validExtraNonce2Values.Length; i++)
        {
            validExtraNonce2LowercaseValues[i] = validExtraNonce2Values[i].ToLowerInvariant();
            parsedNTimeValues[i] = ParseHexUInt32Strict(nTimeValues[i]);
            parsedNonceValues[i] = ParseHexUInt32Strict(nonceValues[i]);
            parsedVersionBitsValues[i] = ParseHexUInt32Strict(versionBitsValues[i]);

            prebuiltDuplicateKeys[i] = BuildDuplicateKey(
                extraNonce1Values[i],
                validExtraNonce2LowercaseValues[i],
                nTimeValues[i],
                nonceValues[i],
                versionBitsValues[i]);
            duplicateSubmits.Add(prebuiltDuplicateKeys[i]);

            var productionInput = BitcoinSubmitValidation.ParseSubmitInput(
                validExtraNonce2Values[i],
                nTimeValues[i],
                nonceValues[i],
                versionBitsValues[i],
                uint.MaxValue,
                expectedExtraNonce2Length);
            prebuiltProductionDuplicateKeys[i] = BitcoinSubmitValidation.CreateDuplicateKey(
                extraNonce1Values[i],
                productionInput.ExtraNonce2,
                productionInput.NTime,
                productionInput.Nonce,
                productionInput.VersionBits,
                productionInput.HasVersionBits);
            productionDuplicateSubmits.Add(prebuiltProductionDuplicateKeys[i]);
            submitRegistration.TryAdd(prebuiltProductionDuplicateKeys[i], true);

            prebuiltStructuredDuplicateKeys[i] = new StructuredDuplicateKey(
                extraNonce1Values[i],
                validExtraNonce2LowercaseValues[i],
                parsedNTimeValues[i],
                parsedNonceValues[i],
                parsedVersionBitsValues[i]);
            structuredDuplicateSubmits.Add(prebuiltStructuredDuplicateKeys[i]);

            prebuiltCustomStructuredDuplicateKeys[i] = new CustomStructuredDuplicateKey(
                extraNonce1Values[i],
                validExtraNonce2LowercaseValues[i],
                parsedNTimeValues[i],
                parsedNonceValues[i],
                true,
                parsedVersionBitsValues[i]);
            customStructuredDuplicateSubmits.Add(prebuiltCustomStructuredDuplicateKeys[i]);
        }

        var legacyInput = BitcoinSubmitValidation.ParseSubmitInput(
            validExtraNonce2LowercaseValues[0],
            nTimeValues[0],
            nonceValues[0],
            null!,
            null,
            expectedExtraNonce2Length);
        submitRegistration.TryAdd(BitcoinSubmitValidation.CreateDuplicateKey(
            extraNonce1Values[0],
            legacyInput.ExtraNonce2,
            legacyInput.NTime,
            legacyInput.Nonce,
            legacyInput.VersionBits,
            legacyInput.HasVersionBits), true);

        ValidateStructuredPrototypeSemantics();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Combined")]
    public string ValidSubmitFields_CombinedStandaloneEquivalent()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var extraNonce2 = validExtraNonce2Values[index];
        var nTime = nTimeValues[index];
        var nonce = nonceValues[index];
        var versionBits = versionBitsValues[index];

        ValidateHex(extraNonce2, expectedExtraNonce2Length);
        var nTimeInt = ParseHexUInt32Strict(nTime);
        var nonceInt = ParseHexUInt32Strict(nonce);
        var versionBitsInt = ParseHexUInt32Strict(versionBits);

        return BuildDuplicateKey(
            extraNonce1Values[index],
            extraNonce2.ToLowerInvariant(),
            ToStringHex8(nTimeInt),
            ToStringHex8(nonceInt),
            ToStringHex8(versionBitsInt));
    }

    [Benchmark]
    [BenchmarkCategory("Combined", "Production")]
    public void Production_ValidSubmitFields_CombinedStructured()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var input = BitcoinSubmitValidation.ParseSubmitInput(
            validExtraNonce2Values[index],
            nTimeValues[index],
            nonceValues[index],
            versionBitsValues[index],
            uint.MaxValue,
            expectedExtraNonce2Length);

        var key = BitcoinSubmitValidation.CreateDuplicateKey(
            extraNonce1Values[index],
            input.ExtraNonce2,
            input.NTime,
            input.Nonce,
            input.VersionBits,
            input.HasVersionBits);

        consumer.Consume(key);
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "Legacy")]
    public uint Production_SubmitInput_ValidateOnly_LegacyValid()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var input = BitcoinSubmitValidation.ParseSubmitInput(
            validExtraNonce2LowercaseValues[index],
            nTimeValues[index],
            nonceValues[index],
            null!,
            null,
            expectedExtraNonce2Length);

        return input.Nonce;
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "VersionRolling")]
    public uint Production_SubmitInput_ValidateOnly_VersionRollingValid()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var input = BitcoinSubmitValidation.ParseSubmitInput(
            validExtraNonce2LowercaseValues[index],
            nTimeValues[index],
            nonceValues[index],
            versionBitsValues[index],
            uint.MaxValue,
            expectedExtraNonce2Length);

        return input.VersionBits;
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "Legacy", "DuplicateKey")]
    public void Production_SubmitInput_ValidateAndBuildDuplicateKey_LegacyValid()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var input = BitcoinSubmitValidation.ParseSubmitInput(
            validExtraNonce2LowercaseValues[index],
            nTimeValues[index],
            nonceValues[index],
            null!,
            null,
            expectedExtraNonce2Length);

        consumer.Consume(BitcoinSubmitValidation.CreateDuplicateKey(
            extraNonce1Values[index],
            input.ExtraNonce2,
            input.NTime,
            input.Nonce,
            input.VersionBits,
            input.HasVersionBits));
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "VersionRolling", "DuplicateKey")]
    public void Production_SubmitInput_ValidateAndBuildDuplicateKey_VersionRollingValid()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var input = BitcoinSubmitValidation.ParseSubmitInput(
            validExtraNonce2LowercaseValues[index],
            nTimeValues[index],
            nonceValues[index],
            versionBitsValues[index],
            uint.MaxValue,
            expectedExtraNonce2Length);

        consumer.Consume(BitcoinSubmitValidation.CreateDuplicateKey(
            extraNonce1Values[index],
            input.ExtraNonce2,
            input.NTime,
            input.Nonce,
            input.VersionBits,
            input.HasVersionBits));
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "Legacy", "DuplicateRegister")]
    public bool Production_SubmitInput_ValidateAndRegisterDuplicate_LegacyDuplicate()
    {
        var input = BitcoinSubmitValidation.ParseSubmitInput(
            validExtraNonce2LowercaseValues[0],
            nTimeValues[0],
            nonceValues[0],
            null!,
            null,
            expectedExtraNonce2Length);
        var key = BitcoinSubmitValidation.CreateDuplicateKey(extraNonce1Values[0], input.ExtraNonce2, input.NTime, input.Nonce, input.VersionBits, input.HasVersionBits);

        return submitRegistration.TryAdd(key, true);
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "VersionRolling", "DuplicateRegister")]
    public bool Production_SubmitInput_ValidateAndRegisterDuplicate_VersionRollingDuplicate()
    {
        var input = BitcoinSubmitValidation.ParseSubmitInput(
            validExtraNonce2LowercaseValues[0],
            nTimeValues[0],
            nonceValues[0],
            versionBitsValues[0],
            uint.MaxValue,
            expectedExtraNonce2Length);
        var key = BitcoinSubmitValidation.CreateDuplicateKey(extraNonce1Values[0], input.ExtraNonce2, input.NTime, input.Nonce, input.VersionBits, input.HasVersionBits);

        return submitRegistration.TryAdd(key, true);
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "Legacy", "DuplicateRegister")]
    public bool Production_SubmitInput_ValidateAndRegisterDuplicate_LegacyNew_ResetPerOperation()
    {
        submitRegistration.Clear();
        var input = BitcoinSubmitValidation.ParseSubmitInput(
            validExtraNonce2LowercaseValues[0],
            nTimeValues[0],
            nonceValues[0],
            null!,
            null,
            expectedExtraNonce2Length);
        var key = BitcoinSubmitValidation.CreateDuplicateKey(extraNonce1Values[0], input.ExtraNonce2, input.NTime, input.Nonce, input.VersionBits, input.HasVersionBits);

        return submitRegistration.TryAdd(key, true);
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "VersionRolling", "DuplicateRegister")]
    public bool Production_SubmitInput_ValidateAndRegisterDuplicate_VersionRollingNew_ResetPerOperation()
    {
        submitRegistration.Clear();
        var input = BitcoinSubmitValidation.ParseSubmitInput(
            validExtraNonce2LowercaseValues[0],
            nTimeValues[0],
            nonceValues[0],
            versionBitsValues[0],
            uint.MaxValue,
            expectedExtraNonce2Length);
        var key = BitcoinSubmitValidation.CreateDuplicateKey(extraNonce1Values[0], input.ExtraNonce2, input.NTime, input.Nonce, input.VersionBits, input.HasVersionBits);

        return submitRegistration.TryAdd(key, true);
    }

    [Benchmark(OperationsPerInvoke = RegistrationBatchSize)]
    [BenchmarkCategory("Production", "SubmitInput", "Legacy", "DuplicateRegister", "Batch")]
    public int Production_SubmitInput_ValidateAndRegisterDuplicate_LegacyNew_Batch()
    {
        batchSubmitRegistration.Clear();
        var added = 0;

        for(var i = 0; i < RegistrationBatchSize; i++)
        {
            var input = BitcoinSubmitValidation.ParseSubmitInput(
                batchExtraNonce2Values[i],
                nTimeValues[0],
                batchNonceValues[i],
                null!,
                null,
                expectedExtraNonce2Length);
            var key = BitcoinSubmitValidation.CreateDuplicateKey(extraNonce1Values[0], input.ExtraNonce2, input.NTime, input.Nonce, input.VersionBits, input.HasVersionBits);

            if(batchSubmitRegistration.TryAdd(key, true))
                added++;
        }

        return added;
    }

    [Benchmark(OperationsPerInvoke = RegistrationBatchSize)]
    [BenchmarkCategory("Production", "SubmitInput", "VersionRolling", "DuplicateRegister", "Batch")]
    public int Production_SubmitInput_ValidateAndRegisterDuplicate_VersionRollingNew_Batch()
    {
        batchSubmitRegistration.Clear();
        var added = 0;

        for(var i = 0; i < RegistrationBatchSize; i++)
        {
            var input = BitcoinSubmitValidation.ParseSubmitInput(
                batchExtraNonce2Values[i],
                nTimeValues[0],
                batchNonceValues[i],
                versionBitsValues[0],
                uint.MaxValue,
                expectedExtraNonce2Length);
            var key = BitcoinSubmitValidation.CreateDuplicateKey(extraNonce1Values[0], input.ExtraNonce2, input.NTime, input.Nonce, input.VersionBits, input.HasVersionBits);

            if(batchSubmitRegistration.TryAdd(key, true))
                added++;
        }

        return added;
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Predicate")]
    public bool StandaloneEquivalent_Hex8Validation_ValidPredicate()
    {
        return IsExactHex(Next(validHex8Values), 8);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Predicate")]
    public bool StandaloneEquivalent_Hex8Validation_InvalidPredicate()
    {
        return IsExactHex(Next(invalidHex8Values), 8);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Predicate")]
    public uint StandaloneEquivalent_Hex8Parse_ValidAfterValidation()
    {
        return ParseHexUInt32Strict(Next(validHex8Values));
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Predicate", "Production")]
    public bool Production_Hex8Validation_Valid()
    {
        return BitcoinSubmitValidation.TryParseHex8Strict(Next(validHex8Values), out _);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Predicate", "Production")]
    public bool Production_Hex8Validation_Invalid()
    {
        return BitcoinSubmitValidation.TryParseHex8Strict(Next(invalidHex8Values), out _);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Production")]
    public uint Production_Hex8ParseStrict_Valid()
    {
        return BitcoinSubmitValidation.ParseHex8Strict(Next(validHex8Values), "incorrect size", "invalid hex");
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Canonicalization")]
    public string StandaloneEquivalent_Hex8Canonicalization_ToStringHex8()
    {
        return ToStringHex8(ParseHexUInt32Strict(Next(validHex8Values)));
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Canonicalization")]
    public string StandaloneEquivalent_ExtraNonce2LowercaseCanonicalization()
    {
        return Next(validExtraNonce2Values).ToLowerInvariant();
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Canonicalization", "Production")]
    public string Production_ExtraNonce2_ValidateAndCanonicalize_Valid()
    {
        var extraNonce2 = Next(validExtraNonce2Values);
        BitcoinSubmitValidation.ValidateHex(extraNonce2, expectedExtraNonce2Length, "incorrect size of extranonce2", "invalid extranonce2");
        return BitcoinSubmitValidation.NormalizeHexLowercaseIfNeeded(extraNonce2);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Canonicalization", "Production", "ExtraNonce2")]
    public string Production_ExtraNonce2_ValidateAndCanonicalize_ValidLowercase_NoAllocation()
    {
        var extraNonce2 = Next(validExtraNonce2LowercaseValues);
        BitcoinSubmitValidation.ValidateHex(extraNonce2, expectedExtraNonce2Length, "incorrect size of extranonce2", "invalid extranonce2");
        return BitcoinSubmitValidation.NormalizeHexLowercaseIfNeeded(extraNonce2);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Canonicalization", "Production", "ExtraNonce2")]
    public string Production_ExtraNonce2_ValidateAndCanonicalize_ValidUppercase_Allocates()
    {
        var extraNonce2 = Next(validExtraNonce2UppercaseValues);
        BitcoinSubmitValidation.ValidateHex(extraNonce2, expectedExtraNonce2Length, "incorrect size of extranonce2", "invalid extranonce2");
        return BitcoinSubmitValidation.NormalizeHexLowercaseIfNeeded(extraNonce2);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Canonicalization", "Production", "ExtraNonce2")]
    public string Production_ExtraNonce2_ValidateAndCanonicalize_MixedCase()
    {
        var extraNonce2 = Next(validExtraNonce2MixedCaseValues);
        BitcoinSubmitValidation.ValidateHex(extraNonce2, expectedExtraNonce2Length, "incorrect size of extranonce2", "invalid extranonce2");
        return BitcoinSubmitValidation.NormalizeHexLowercaseIfNeeded(extraNonce2);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Production", "ExtraNonce2")]
    public int Production_ExtraNonce2_ValidateOnly_ValidLowercase()
    {
        var extraNonce2 = Next(validExtraNonce2LowercaseValues);
        BitcoinSubmitValidation.ValidateHex(extraNonce2, expectedExtraNonce2Length, "incorrect size of extranonce2", "invalid extranonce2");
        return extraNonce2.Length;
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Production", "ExtraNonce2")]
    public int Production_ExtraNonce2_ValidateOnly_ValidUppercase()
    {
        var extraNonce2 = Next(validExtraNonce2UppercaseValues);
        BitcoinSubmitValidation.ValidateHex(extraNonce2, expectedExtraNonce2Length, "incorrect size of extranonce2", "invalid extranonce2");
        return extraNonce2.Length;
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "ExceptionPath", "Production", "ExtraNonce2")]
    public int Production_ExtraNonce2_ValidateAndCanonicalize_InvalidNonHex()
    {
        try
        {
            var extraNonce2 = Next(invalidExtraNonce2NonHexValues);
            BitcoinSubmitValidation.ValidateHex(extraNonce2, expectedExtraNonce2Length, "incorrect size of extranonce2", "invalid extranonce2");
            return BitcoinSubmitValidation.NormalizeHexLowercaseIfNeeded(extraNonce2).Length;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "ExceptionPath", "Production", "ExtraNonce2")]
    public int Production_ExtraNonce2_ValidateAndCanonicalize_InvalidLength()
    {
        try
        {
            var extraNonce2 = Next(invalidExtraNonce2LengthValues);
            BitcoinSubmitValidation.ValidateHex(extraNonce2, expectedExtraNonce2Length, "incorrect size of extranonce2", "invalid extranonce2");
            return BitcoinSubmitValidation.NormalizeHexLowercaseIfNeeded(extraNonce2).Length;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey")]
    public string StandaloneEquivalent_DuplicateKey_ConstructStringJoin()
    {
        var index = NextIndex(validExtraNonce2Values.Length);

        return BuildDuplicateKey(
            extraNonce1Values[index],
            validExtraNonce2Values[index],
            nTimeValues[index],
            nonceValues[index],
            versionBitsValues[index]);
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey")]
    public bool StandaloneEquivalent_DuplicateKey_LookupPrebuiltKey()
    {
        return duplicateSubmits.Contains(Next(prebuiltDuplicateKeys));
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey")]
    public bool StandaloneEquivalent_DuplicateKey_ConstructAndLookupStringJoin()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var key = BuildDuplicateKey(
            extraNonce1Values[index],
            validExtraNonce2LowercaseValues[index],
            nTimeValues[index],
            nonceValues[index],
            versionBitsValues[index]);

        return duplicateSubmits.Contains(key);
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey", "Production")]
    public void Production_DuplicateKey_ConstructStructured()
    {
        var index = NextIndex(validExtraNonce2Values.Length);

        var key = BitcoinSubmitValidation.CreateDuplicateKey(
            extraNonce1Values[index],
            validExtraNonce2LowercaseValues[index],
            parsedNTimeValues[index],
            parsedNonceValues[index],
            parsedVersionBitsValues[index],
            true);

        consumer.Consume(key);
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey", "Production")]
    public bool Production_DuplicateKey_LookupStructured()
    {
        return productionDuplicateSubmits.Contains(Next(prebuiltProductionDuplicateKeys));
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey", "Production")]
    public bool Production_DuplicateKey_ConstructAndLookupStructured()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var key = BitcoinSubmitValidation.CreateDuplicateKey(
            extraNonce1Values[index],
            validExtraNonce2LowercaseValues[index],
            parsedNTimeValues[index],
            parsedNonceValues[index],
            parsedVersionBitsValues[index],
            true);

        return productionDuplicateSubmits.Contains(key);
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey", "Prototype")]
    public StructuredDuplicateKey Prototype_DuplicateKey_ConstructStructured()
    {
        var index = NextIndex(validExtraNonce2Values.Length);

        return new StructuredDuplicateKey(
            extraNonce1Values[index],
            validExtraNonce2LowercaseValues[index],
            parsedNTimeValues[index],
            parsedNonceValues[index],
            parsedVersionBitsValues[index]);
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey", "Prototype")]
    public bool Prototype_DuplicateKey_LookupStructured()
    {
        return structuredDuplicateSubmits.Contains(Next(prebuiltStructuredDuplicateKeys));
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey", "Prototype")]
    public bool Prototype_DuplicateKey_ConstructAndLookupStructured()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var key = new StructuredDuplicateKey(
            extraNonce1Values[index],
            validExtraNonce2LowercaseValues[index],
            parsedNTimeValues[index],
            parsedNonceValues[index],
            parsedVersionBitsValues[index]);

        return structuredDuplicateSubmits.Contains(key);
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey", "Prototype")]
    public CustomStructuredDuplicateKey Prototype_DuplicateKey_ConstructCustomStructured()
    {
        var index = NextIndex(validExtraNonce2Values.Length);

        return new CustomStructuredDuplicateKey(
            extraNonce1Values[index],
            validExtraNonce2LowercaseValues[index],
            parsedNTimeValues[index],
            parsedNonceValues[index],
            true,
            parsedVersionBitsValues[index]);
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey", "Prototype")]
    public bool Prototype_DuplicateKey_LookupCustomStructured()
    {
        return customStructuredDuplicateSubmits.Contains(Next(prebuiltCustomStructuredDuplicateKeys));
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey", "Prototype")]
    public bool Prototype_DuplicateKey_ConstructAndLookupCustomStructured()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var key = new CustomStructuredDuplicateKey(
            extraNonce1Values[index],
            validExtraNonce2LowercaseValues[index],
            parsedNTimeValues[index],
            parsedNonceValues[index],
            true,
            parsedVersionBitsValues[index]);

        return customStructuredDuplicateSubmits.Contains(key);
    }

    [Benchmark]
    [BenchmarkCategory("Combined", "Prototype")]
    public StructuredDuplicateKey Prototype_ValidSubmitFields_CombinedStructured()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var extraNonce2 = validExtraNonce2Values[index];
        var nTime = nTimeValues[index];
        var nonce = nonceValues[index];
        var versionBits = versionBitsValues[index];

        ValidateHex(extraNonce2, expectedExtraNonce2Length);
        var nTimeInt = ParseHexUInt32Strict(nTime);
        var nonceInt = ParseHexUInt32Strict(nonce);
        var versionBitsInt = ParseHexUInt32Strict(versionBits);

        return new StructuredDuplicateKey(
            extraNonce1Values[index],
            extraNonce2.ToLowerInvariant(),
            nTimeInt,
            nonceInt,
            versionBitsInt);
    }

    [Benchmark]
    [BenchmarkCategory("Combined", "Prototype")]
    public CustomStructuredDuplicateKey Prototype_ValidSubmitFields_CombinedCustomStructured()
    {
        var index = NextIndex(validExtraNonce2Values.Length);
        var extraNonce2 = validExtraNonce2Values[index];
        var nTime = nTimeValues[index];
        var nonce = nonceValues[index];
        var versionBits = versionBitsValues[index];

        ValidateHex(extraNonce2, expectedExtraNonce2Length);
        var nTimeInt = ParseHexUInt32Strict(nTime);
        var nonceInt = ParseHexUInt32Strict(nonce);
        var versionBitsInt = ParseHexUInt32Strict(versionBits);

        return new CustomStructuredDuplicateKey(
            extraNonce1Values[index],
            extraNonce2.ToLowerInvariant(),
            nTimeInt,
            nonceInt,
            true,
            versionBitsInt);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "ExceptionPath")]
    public int StandaloneEquivalent_MalformedHex8_ThrowsExceptionPath()
    {
        try
        {
            ValidateHex8OrThrow(Next(invalidHex8Values));
            return 0;
        }
        catch(SubmitInputValidationException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "ExceptionPath", "Production")]
    public int Production_MalformedHex8_ExceptionPath()
    {
        try
        {
            BitcoinSubmitValidation.ParseHex8Strict(Next(invalidHex8Values), "incorrect size", "invalid hex");
            return 0;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "ExceptionPath")]
    public int StandaloneEquivalent_MalformedExtraNonce2_ThrowsBeforeDuplicateKeyExceptionPath()
    {
        try
        {
            ValidateHexOrThrow(Next(invalidExtraNonce2Values), expectedExtraNonce2Length, "invalid extranonce2");
            return 0;
        }
        catch(SubmitInputValidationException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "ExceptionPath", "Production")]
    public int Production_MalformedExtraNonce2_ExceptionPath()
    {
        try
        {
            BitcoinSubmitValidation.ValidateHex(
                Next(invalidExtraNonce2Values),
                expectedExtraNonce2Length,
                "incorrect size of extranonce2",
                "invalid extranonce2");
            return 0;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "ExceptionPath")]
    public int Production_SubmitInput_MalformedNTime_ExceptionPath()
    {
        try
        {
            BitcoinSubmitValidation.ParseSubmitInput(
                validExtraNonce2LowercaseValues[0],
                Next(invalidHex8Values),
                nonceValues[0],
                versionBitsValues[0],
                uint.MaxValue,
                expectedExtraNonce2Length);
            return 0;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "ExceptionPath")]
    public int Production_SubmitInput_MalformedNonce_ExceptionPath()
    {
        try
        {
            BitcoinSubmitValidation.ParseSubmitInput(
                validExtraNonce2LowercaseValues[0],
                nTimeValues[0],
                Next(invalidHex8Values),
                versionBitsValues[0],
                uint.MaxValue,
                expectedExtraNonce2Length);
            return 0;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "ExceptionPath")]
    public int Production_SubmitInput_MalformedVersionBits_ExceptionPath()
    {
        try
        {
            BitcoinSubmitValidation.ParseSubmitInput(
                validExtraNonce2LowercaseValues[0],
                nTimeValues[0],
                nonceValues[0],
                Next(invalidHex8Values),
                uint.MaxValue,
                expectedExtraNonce2Length);
            return 0;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("Production", "SubmitInput", "ExceptionPath")]
    public int Production_SubmitInput_MalformedExtraNonce2_ExceptionPath()
    {
        try
        {
            BitcoinSubmitValidation.ParseSubmitInput(
                Next(invalidExtraNonce2Values),
                nTimeValues[0],
                nonceValues[0],
                versionBitsValues[0],
                uint.MaxValue,
                expectedExtraNonce2Length);
            return 0;
        }
        catch(StratumException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("SubmitShape")]
    public int StandaloneEquivalent_LegacySubmitShape_Valid5Params_NoVersionRolling()
    {
        return ValidateSubmitShape(Next(validLegacySubmitParams), false);
    }

    [Benchmark]
    [BenchmarkCategory("SubmitShape")]
    public int StandaloneEquivalent_VersionRollingSubmitShape_Valid6Params()
    {
        return ValidateSubmitShape(Next(validVersionRollingSubmitParams), true);
    }

    [Benchmark]
    [BenchmarkCategory("SubmitShape", "ExceptionPath")]
    public int StandaloneEquivalent_ExtraVersionBitsWithoutNegotiation_ExceptionPath()
    {
        try
        {
            return ValidateSubmitShape(invalidSubmitParams[0], false);
        }
        catch(SubmitInputValidationException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("SubmitShape", "ExceptionPath")]
    public int StandaloneEquivalent_MissingVersionBitsWhenRequired_ExceptionPath()
    {
        try
        {
            return ValidateSubmitShape(validLegacySubmitParams[0], true);
        }
        catch(SubmitInputValidationException ex)
        {
            return ex.Message.Length;
        }
    }

    [Benchmark]
    [BenchmarkCategory("SubmitShape", "ExceptionPath")]
    public int StandaloneEquivalent_NonStringSubmitParam_ExceptionPath()
    {
        try
        {
            return ValidateSubmitShape(invalidSubmitParams[2], false);
        }
        catch(SubmitInputValidationException ex)
        {
            return ex.Message.Length;
        }
    }

    private static string BuildDuplicateKey(string extraNonce1, string extraNonce2, string nTime, string nonce, string? versionBits)
    {
        return string.Join(':', extraNonce1, extraNonce2, nTime, nonce, versionBits ?? string.Empty);
    }

    private static uint ParseHexUInt32Strict(string value)
    {
        if(!TryParseHexUInt32Strict(value, out var result))
            throw new SubmitInputValidationException("invalid hex8");

        return result;
    }

    private static bool TryParseHexUInt32Strict(string value, out uint result)
    {
        result = 0;

        if(!IsExactHex(value, 8))
            return false;

        for(var i = 0; i < value.Length; i++)
        {
            result <<= 4;
            result |= FromHex(value[i]);
        }

        return true;
    }

    private static void ValidateHex(string value, int expectedLength)
    {
        if(!IsExactHex(value, expectedLength))
            throw new SubmitInputValidationException("invalid hex");
    }

    private static void ValidateHex8OrThrow(string value)
    {
        if(!IsExactHex(value, 8))
            throw new SubmitInputValidationException("invalid hex8");
    }

    private static void ValidateHexOrThrow(string value, int expectedLength, string message)
    {
        if(!IsExactHex(value, expectedLength))
            throw new SubmitInputValidationException(message);
    }

    private static bool IsExactHex(string value, int expectedLength)
    {
        if(value.Length != expectedLength)
            return false;

        for(var i = 0; i < value.Length; i++)
        {
            if(!IsHex(value[i]))
                return false;
        }

        return true;
    }

    private static bool IsHex(char c)
    {
        return c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    private static uint FromHex(char c)
    {
        if(c is >= '0' and <= '9')
            return (uint) (c - '0');

        if(c is >= 'a' and <= 'f')
            return (uint) (c - 'a' + 10);

        return (uint) (c - 'A' + 10);
    }

    private static string ToStringHex8(uint value)
    {
        return value.ToString("x8");
    }

    private static int ValidateSubmitShape(object[] submitParams, bool versionRollingNegotiated)
    {
        var expectedLength = versionRollingNegotiated ? 6 : 5;

        if(submitParams.Length != expectedLength)
        {
            if(versionRollingNegotiated && submitParams.Length == 5)
                throw new SubmitInputValidationException("missing version bits");

            if(!versionRollingNegotiated && submitParams.Length == 6)
                throw new SubmitInputValidationException("version rolling was not negotiated");

            throw new SubmitInputValidationException("invalid params");
        }

        for(var i = 0; i < submitParams.Length; i++)
        {
            if(submitParams[i] is not string)
                throw new SubmitInputValidationException("invalid params");
        }

        return submitParams.Length;
    }

    private string Next(string[] values)
    {
        return values[NextIndex(values.Length)];
    }

    private object[] Next(object[][] values)
    {
        var index = unchecked(shapeIndex++);
        return values[(int) ((uint) index % (uint) values.Length)];
    }

    private StructuredDuplicateKey Next(StructuredDuplicateKey[] values)
    {
        var index = unchecked(keyIndex++);
        return values[(int) ((uint) index % (uint) values.Length)];
    }

    private CustomStructuredDuplicateKey Next(CustomStructuredDuplicateKey[] values)
    {
        var index = unchecked(keyIndex++);
        return values[(int) ((uint) index % (uint) values.Length)];
    }

    private BitcoinSubmitDuplicateKey Next(BitcoinSubmitDuplicateKey[] values)
    {
        var index = unchecked(keyIndex++);
        return values[(int) ((uint) index % (uint) values.Length)];
    }

    private int NextIndex(int length)
    {
        var index = unchecked(keyIndex++);
        return (int) ((uint) index % (uint) length);
    }

    private sealed class SubmitInputValidationException(string message) : Exception(message);

    private void ValidateStructuredPrototypeSemantics()
    {
        var nullableA = new StructuredDuplicateKey("aa", "bb", 1, 2, 3);
        var nullableSame = new StructuredDuplicateKey("aa", "bb", 1, 2, 3);
        var nullableDifferentVersion = new StructuredDuplicateKey("aa", "bb", 1, 2, 4);
        var nullableDifferentNonce = new StructuredDuplicateKey("aa", "bb", 1, 3, 3);
        var nullableLegacyA = new StructuredDuplicateKey("aa", "bb", 1, 2, null);
        var nullableLegacySame = new StructuredDuplicateKey("aa", "bb", 1, 2, null);
        var nullableDifferentCase = new StructuredDuplicateKey("aa", "BB", 1, 2, 3);

        if(!nullableA.Equals(nullableSame) || nullableA.GetHashCode() != nullableSame.GetHashCode())
            throw new InvalidOperationException("Structured duplicate key equality is inconsistent");

        if(nullableA.Equals(nullableDifferentVersion) || nullableA.Equals(nullableDifferentNonce) || !nullableLegacyA.Equals(nullableLegacySame) || nullableA.Equals(nullableDifferentCase))
            throw new InvalidOperationException("Structured duplicate key semantics are inconsistent");

        var customA = new CustomStructuredDuplicateKey("aa", "bb", 1, 2, true, 3);
        var customSame = new CustomStructuredDuplicateKey("aa", "bb", 1, 2, true, 3);
        var customDifferentVersion = new CustomStructuredDuplicateKey("aa", "bb", 1, 2, true, 4);
        var customDifferentNonce = new CustomStructuredDuplicateKey("aa", "bb", 1, 3, true, 3);
        var customLegacyA = new CustomStructuredDuplicateKey("aa", "bb", 1, 2, false, 0);
        var customLegacySame = new CustomStructuredDuplicateKey("aa", "bb", 1, 2, false, 9);
        var customDifferentCase = new CustomStructuredDuplicateKey("aa", "BB", 1, 2, true, 3);

        if(!customA.Equals(customSame) || customA.GetHashCode() != customSame.GetHashCode())
            throw new InvalidOperationException("Custom duplicate key equality is inconsistent");

        if(customA.Equals(customDifferentVersion) || customA.Equals(customDifferentNonce) || !customLegacyA.Equals(customLegacySame) || customA.Equals(customDifferentCase))
            throw new InvalidOperationException("Custom duplicate key semantics are inconsistent");
    }

    public readonly struct StructuredDuplicateKey : IEquatable<StructuredDuplicateKey>
    {
        private readonly string extraNonce1;
        private readonly string extraNonce2;
        private readonly uint nTime;
        private readonly uint nonce;
        private readonly uint? versionBits;

        public StructuredDuplicateKey(string extraNonce1, string extraNonce2, uint nTime, uint nonce, uint? versionBits)
        {
            this.extraNonce1 = extraNonce1;
            this.extraNonce2 = extraNonce2;
            this.nTime = nTime;
            this.nonce = nonce;
            this.versionBits = versionBits;
        }

        public bool Equals(StructuredDuplicateKey other)
        {
            return string.Equals(extraNonce1, other.extraNonce1, StringComparison.Ordinal) &&
                string.Equals(extraNonce2, other.extraNonce2, StringComparison.Ordinal) &&
                nTime == other.nTime &&
                nonce == other.nonce &&
                versionBits == other.versionBits;
        }

        public override bool Equals(object? obj)
        {
            return obj is StructuredDuplicateKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(extraNonce1, extraNonce2, nTime, nonce, versionBits);
        }
    }

    public readonly struct CustomStructuredDuplicateKey : IEquatable<CustomStructuredDuplicateKey>
    {
        private readonly string extraNonce1;
        private readonly string extraNonce2;
        private readonly uint nTime;
        private readonly uint nonce;
        private readonly bool hasVersionBits;
        private readonly uint versionBits;

        public CustomStructuredDuplicateKey(
            string extraNonce1,
            string extraNonce2,
            uint nTime,
            uint nonce,
            bool hasVersionBits,
            uint versionBits)
        {
            this.extraNonce1 = extraNonce1;
            this.extraNonce2 = extraNonce2;
            this.nTime = nTime;
            this.nonce = nonce;
            this.hasVersionBits = hasVersionBits;
            this.versionBits = hasVersionBits ? versionBits : 0;
        }

        public bool Equals(CustomStructuredDuplicateKey other)
        {
            return nTime == other.nTime &&
                nonce == other.nonce &&
                hasVersionBits == other.hasVersionBits &&
                versionBits == other.versionBits &&
                StringComparer.Ordinal.Equals(extraNonce1, other.extraNonce1) &&
                StringComparer.Ordinal.Equals(extraNonce2, other.extraNonce2);
        }

        public override bool Equals(object? obj)
        {
            return obj is CustomStructuredDuplicateKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(extraNonce1, StringComparer.Ordinal);
            hash.Add(extraNonce2, StringComparer.Ordinal);
            hash.Add(nTime);
            hash.Add(nonce);
            hash.Add(hasVersionBits);
            hash.Add(versionBits);
            return hash.ToHashCode();
        }
    }
}
