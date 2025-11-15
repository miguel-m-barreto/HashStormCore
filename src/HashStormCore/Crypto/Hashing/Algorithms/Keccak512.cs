using HashStormCore.Contracts;
using HashStormCore.Native;

namespace HashStormCore.Crypto.Hashing.Algorithms;

[Identifier("keccak512")]
public unsafe class Keccak512 : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        fixed (byte* input = data)
        {
            fixed (byte* output = result)
            {
                Multihash.keccak512(input, output, (uint) data.Length);
            }
        }
    }
}
