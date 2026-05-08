using System.Runtime.CompilerServices;
using HashStormCore.Stratum;

namespace HashStormCore.Blockchain.Bitcoin;

internal readonly record struct BitcoinSubmitInput(
    string ExtraNonce2,
    uint NTime,
    uint Nonce,
    uint VersionBits,
    bool HasVersionBits);

internal readonly record struct BitcoinSubmitDuplicateKey(
    string ExtraNonce1,
    string ExtraNonce2,
    uint NTime,
    uint Nonce,
    uint? VersionBits);

internal static class BitcoinSubmitValidation
{
    internal static BitcoinSubmitInput ParseSubmitInput(
        string extraNonce2,
        string nTime,
        string nonce,
        string versionBits,
        uint? versionRollingMask,
        int expectedExtraNonce2Length)
    {
        if(string.IsNullOrEmpty(extraNonce2))
            throw new StratumException(StratumError.Other, "missing or invalid extranonce2");

        if(string.IsNullOrEmpty(nTime))
            throw new StratumException(StratumError.Other, "missing or invalid ntime");

        if(string.IsNullOrEmpty(nonce))
            throw new StratumException(StratumError.Other, "missing or invalid nonce");

        var nTimeInt = ParseHex8Strict(nTime, "incorrect size of ntime", "invalid ntime");
        var nonceInt = ParseHex8Strict(nonce, "incorrect size of nonce", "invalid nonce");
        var versionBitsInt = 0u;
        var hasVersionBits = false;

        if(versionRollingMask.HasValue)
        {
            if(versionBits == null)
                throw new StratumException(StratumError.Other, "missing version bits");

            versionBitsInt = ParseHex8Strict(versionBits, "incorrect size of version bits", "invalid version bits");

            if((versionBitsInt & ~versionRollingMask.Value) != 0)
                throw new StratumException(StratumError.Other, "rolling-version mask violation");

            hasVersionBits = true;
        }
        else if(versionBits != null)
        {
            throw new StratumException(StratumError.Other, "version rolling was not negotiated");
        }

        ValidateHex(extraNonce2, expectedExtraNonce2Length, "incorrect size of extranonce2", "invalid extranonce2");

        return new BitcoinSubmitInput(
            NormalizeHexLowercaseIfNeeded(extraNonce2),
            nTimeInt,
            nonceInt,
            versionBitsInt,
            hasVersionBits);
    }

    internal static BitcoinSubmitDuplicateKey CreateDuplicateKey(
        string extraNonce1,
        string extraNonce2,
        uint nTime,
        uint nonce,
        uint versionBits,
        bool hasVersionBits)
    {
        return new BitcoinSubmitDuplicateKey(
            NormalizeHexLowercaseIfNeeded(extraNonce1 ?? string.Empty),
            NormalizeHexLowercaseIfNeeded(extraNonce2 ?? string.Empty),
            nTime,
            nonce,
            hasVersionBits ? versionBits : null);
    }

    internal static uint ParseHex8Strict(string value, string sizeError, string invalidError)
    {
        if(value == null || value.Length != 8)
            throw new StratumException(StratumError.Other, sizeError);

        if(!TryParseHex8Strict(value, out var result))
            throw new StratumException(StratumError.Other, invalidError);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseHex8Strict(string value, out uint result)
    {
        result = 0;

        if(value == null || value.Length != 8)
            return false;

        uint acc = 0;

        for(var i = 0; i < 8; i++)
        {
            var c = (uint) value[i];
            var digit = c - '0';
            uint nibble;

            if(digit <= 9)
            {
                nibble = digit;
            }
            else
            {
                var lower = (c | 0x20) - 'a';

                if(lower > 5)
                    return false;

                nibble = lower + 10;
            }

            acc = (acc << 4) | nibble;
        }

        result = acc;
        return true;
    }

    internal static void ValidateHex(string value, int expectedLength, string sizeError, string invalidError)
    {
        if(value == null || value.Length != expectedLength)
            throw new StratumException(StratumError.Other, sizeError);

        for(var i = 0; i < value.Length; i++)
        {
            var c = (uint) value[i];
            var digit = c - '0';

            if(digit <= 9)
                continue;

            var lower = (c | 0x20) - 'a';

            if(lower > 5)
                throw new StratumException(StratumError.Other, invalidError);
        }
    }

    internal static string NormalizeHexLowercaseIfNeeded(string value)
    {
        if(string.IsNullOrEmpty(value))
            return value;

        for(var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if((uint) (c - 'A') <= 'F' - 'A')
            {
                return string.Create(value.Length, value, static (destination, source) =>
                {
                    for(var j = 0; j < source.Length; j++)
                    {
                        var ch = source[j];
                        destination[j] = (uint) (ch - 'A') <= 'F' - 'A'
                            ? (char) (ch | 0x20)
                            : ch;
                    }
                });
            }
        }

        return value;
    }
}
