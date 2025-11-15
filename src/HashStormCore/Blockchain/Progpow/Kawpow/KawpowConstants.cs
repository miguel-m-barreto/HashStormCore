using System.Numerics;

namespace HashStormCore.Blockchain.Progpow.Kawpow

{
    /// <summary>
    /// Shared parameters for KawPoW-family coins (RVN, NEOX, CLORE, XNA).
    /// </summary>
    public static class KawpowConstants
    {
        /// <summary>Epoch length in blocks (Ravencoin-style).</summary>
        public const int EpochLength = 7500;

        /// <summary>Length of extranonce placeholder (bytes) embedded in coinbase tag.</summary>
        public const int ExtranoncePlaceHolderLength = 2;

        /// <summary>
        /// Reuse Ravencoin's Diff1 to ensure identical target math across all KawPoW-family coins.
        /// </summary>
        public static readonly BigInteger Diff1 = HashStormCore.Blockchain.Progpow.RavencoinConstants.Diff1;
    }
}
