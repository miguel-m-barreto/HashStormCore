using HashStormCore.Native;
using static HashStormCore.Native.Cryptonight.Algorithm;

namespace HashStormCore.Crypto.Hashing.Algorithms;

[Identifier("mike")]
public class Mike : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Cryptonight.CryptonightHash(data, result, GHOSTRIDER_MIKE, 0);
    }
}
