using System.Globalization;
using System.Numerics;
using HashStormCore.Util;

namespace HashStormCore.Blockchain.Progpow;

public class FiroConstants
{
    public const int EpochLength = 1300;
    public static BigInteger BigMaxValue = BigInteger.Pow(2, 256);
    public static readonly BigInteger Diff1B = BigInteger.Parse("00000000ffff0000000000000000000000000000000000000000000000000000", NumberStyles.AllowHexSpecifier, null);
    public static readonly BigInteger Diff1 = BigInteger.Parse("00000000ffff0000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public const int ExtranoncePlaceHolderLength = 2;
    public static double Multiplier = (double) new BigRational(BigMaxValue, Diff1);
}

public class RavencoinConstants
{
    public const int EpochLength = 7500;
    public static BigInteger BigMaxValue = BigInteger.Pow(2, 256);
    public static readonly BigInteger Diff1B = BigInteger.Parse("00000000ff000000000000000000000000000000000000000000000000000000", NumberStyles.AllowHexSpecifier, null);
    public static readonly BigInteger Diff1 = BigInteger.Parse("00000000ff000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public const int ExtranoncePlaceHolderLength = 2;
    public static double Multiplier = (double) new BigRational(BigMaxValue, Diff1);
}

public class PhicoinConstants
{
    public const int EpochLength = 2102400;
    public static BigInteger BigMaxValue = BigInteger.Pow(2, 256);
    public static readonly BigInteger Diff1B = BigInteger.Parse("00000000ff000000000000000000000000000000000000000000000000000000", NumberStyles.AllowHexSpecifier, null);
    public static readonly BigInteger Diff1 = BigInteger.Parse("00000000ff000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public const int ExtranoncePlaceHolderLength = 2;
    public static double Multiplier = (double) new BigRational(BigMaxValue, Diff1);
}

public class StakeCubeCoinConstants
{
    public const int EpochLength = 3240;
    public static BigInteger BigMaxValue = BigInteger.Pow(2, 256);
    public static readonly BigInteger Diff1B = BigInteger.Parse("00000000ffff0000000000000000000000000000000000000000000000000000", NumberStyles.AllowHexSpecifier, null);
    public static readonly BigInteger Diff1 = BigInteger.Parse("00000000ffff0000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public const int ExtranoncePlaceHolderLength = 2;
    public static double Multiplier = (double) new BigRational(BigMaxValue, Diff1);
}

public class TelestaiConstants
{
    public const int EpochLength = 27500;
    public static BigInteger BigMaxValue = BigInteger.Pow(2, 256);
    public static readonly BigInteger Diff1B = BigInteger.Parse("00000000ff000000000000000000000000000000000000000000000000000000", NumberStyles.AllowHexSpecifier, null);
    public static readonly BigInteger Diff1 = BigInteger.Parse("00000000ff000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public const int ExtranoncePlaceHolderLength = 2;
    public static double Multiplier = (double) new BigRational(BigMaxValue, Diff1);
}