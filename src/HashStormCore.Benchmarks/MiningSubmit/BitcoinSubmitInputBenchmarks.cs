using BenchmarkDotNet.Attributes;

namespace HashStormCore.Benchmarks.MiningSubmit;

[MemoryDiagnoser]
[CategoriesColumn]
[BenchmarkCategory("MiningSubmit", "Bitcoin", "InputValidation")]
public class BitcoinSubmitInputBenchmarks
{
    private readonly HashSet<string> duplicateSubmits = new(StringComparer.Ordinal);

    private int expectedExtraNonce2Length;
    private int keyIndex;
    private int shapeIndex;

    private string[] extraNonce1Values = null!;
    private string[] validExtraNonce2Values = null!;
    private string[] invalidExtraNonce2Values = null!;
    private string[] validHex8Values = null!;
    private string[] invalidHex8Values = null!;
    private string[] nTimeValues = null!;
    private string[] nonceValues = null!;
    private string[] versionBitsValues = null!;
    private string[] prebuiltDuplicateKeys = null!;
    private object[][] validLegacySubmitParams = null!;
    private object[][] validVersionRollingSubmitParams = null!;
    private object[][] invalidSubmitParams = null!;

    [GlobalSetup]
    public void Setup()
    {
        expectedExtraNonce2Length = 8;
        extraNonce1Values = ["f00dbabe", "cafefeed", "00112233", "89abcdef"];
        validExtraNonce2Values = ["01000000", "02000000", "a1b2c3d4", "FEEDBEEF"];
        invalidExtraNonce2Values = ["zzzzzzzz", "0100000", "010000000", "0100000 "];
        validHex8Values = ["65f1a2b3", "5103677a", "20000000", "00000001"];
        invalidHex8Values = ["65f1a2b ", "5103677z", " 0002000", "abcdef0"];
        nTimeValues = ["65f1a2b3", "65f1a2b4", "65f1a2b5", "65f1a2b6"];
        nonceValues = ["5103677a", "5103677b", "5103677c", "5103677d"];
        versionBitsValues = ["20000000", "20000001", "20000002", "20000003"];
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

        prebuiltDuplicateKeys = new string[validExtraNonce2Values.Length];
        for(var i = 0; i < validExtraNonce2Values.Length; i++)
        {
            prebuiltDuplicateKeys[i] = BuildDuplicateKey(
                extraNonce1Values[i],
                validExtraNonce2Values[i].ToLowerInvariant(),
                nTimeValues[i],
                nonceValues[i],
                versionBitsValues[i]);
            duplicateSubmits.Add(prebuiltDuplicateKeys[i]);
        }
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
    [BenchmarkCategory("InputValidation", "Predicate")]
    public bool Hex8Validation_ValidPredicate()
    {
        return IsExactHex(Next(validHex8Values), 8);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Predicate")]
    public bool Hex8Validation_InvalidPredicate()
    {
        return IsExactHex(Next(invalidHex8Values), 8);
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Predicate")]
    public uint Hex8Parse_ValidAfterValidation()
    {
        return ParseHexUInt32Strict(Next(validHex8Values));
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Canonicalization")]
    public string Hex8Canonicalization_ToStringHex8()
    {
        return ToStringHex8(ParseHexUInt32Strict(Next(validHex8Values)));
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "Canonicalization")]
    public string ExtraNonce2LowercaseCanonicalization()
    {
        return Next(validExtraNonce2Values).ToLowerInvariant();
    }

    [Benchmark]
    [BenchmarkCategory("DuplicateKey")]
    public string DuplicateKey_ConstructStringJoin()
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
    public bool DuplicateKey_LookupPrebuiltKey()
    {
        return duplicateSubmits.Contains(Next(prebuiltDuplicateKeys));
    }

    [Benchmark]
    [BenchmarkCategory("InputValidation", "ExceptionPath")]
    public int MalformedHex8_ThrowsExceptionPath()
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
    [BenchmarkCategory("InputValidation", "ExceptionPath")]
    public int MalformedExtraNonce2_ThrowsBeforeDuplicateKeyExceptionPath()
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
    [BenchmarkCategory("SubmitShape")]
    public int LegacySubmitShape_Valid5Params_NoVersionRolling()
    {
        return ValidateSubmitShape(Next(validLegacySubmitParams), false);
    }

    [Benchmark]
    [BenchmarkCategory("SubmitShape")]
    public int VersionRollingSubmitShape_Valid6Params()
    {
        return ValidateSubmitShape(Next(validVersionRollingSubmitParams), true);
    }

    [Benchmark]
    [BenchmarkCategory("SubmitShape", "ExceptionPath")]
    public int ExtraVersionBitsWithoutNegotiation_ExceptionPath()
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
    public int MissingVersionBitsWhenRequired_ExceptionPath()
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
    public int NonStringSubmitParam_ExceptionPath()
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

    private int NextIndex(int length)
    {
        var index = unchecked(keyIndex++);
        return (int) ((uint) index % (uint) length);
    }

    private sealed class SubmitInputValidationException(string message) : Exception(message);
}
