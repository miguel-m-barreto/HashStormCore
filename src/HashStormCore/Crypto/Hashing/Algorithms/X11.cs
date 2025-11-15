using HashStormCore.Contracts;
using HashStormCore.Native;

namespace HashStormCore.Crypto.Hashing.Algorithms;

[Identifier("x11")]
public unsafe class X11 : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        fixed (byte* input = data)
        {
            fixed (byte* output = result)
            {
                Multihash.x11(input, output, (uint) data.Length);
            }
        }
    }
}
