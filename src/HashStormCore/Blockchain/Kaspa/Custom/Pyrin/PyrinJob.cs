using System.Text;
using HashStormCore.Crypto;
using HashStormCore.Crypto.Hashing.Algorithms;
using HashStormCore.Extensions;

namespace HashStormCore.Blockchain.Kaspa.Custom.Pyrin;

public class PyrinJob : KaspaJob
{
    public PyrinJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher) : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
    {
    }
}