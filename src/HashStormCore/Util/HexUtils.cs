namespace HashStormCore.Util;

public static class HexUtils
{
    public static bool IsFixedLengthHex(ReadOnlySpan<char> value, int length)
    {
        if(value.Length != length)
            return false;

        for(var i = 0; i < value.Length; i++)
        {
            var c = (uint) value[i];
            var digit = c - '0';

            if(digit <= 9)
                continue;

            var lower = (c | 0x20) - 'a';

            if(lower > 5)
                return false;
        }

        return true;
    }
}
