using HashStormCore.Configuration;

namespace HashStormCore.Crypto;

public interface IHashAlgorithm
{
    void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra);
}

public interface IHashAlgorithmInit
{
    bool DigestInit(PoolConfig poolConfig);
}
