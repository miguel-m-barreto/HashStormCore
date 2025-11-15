using HashStormCore.Contracts;
using HashStormCore.Native;

namespace HashStormCore.Crypto.Hashing.Algorithms;

[Identifier("qubit")]
public unsafe class Qubit : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        fixed (byte* input = data)
        {
            fixed (byte* output = result)
            {
                Multihash.qubit(input, output, (uint) data.Length);
            }
        }
    }
}
