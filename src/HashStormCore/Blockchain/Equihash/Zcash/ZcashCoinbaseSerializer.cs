using NBitcoin;

namespace HashStormCore.Blockchain.Equihash.Zcash;

internal static class ZcashCoinbaseSerializer
{
    private static readonly byte[] Sha256Empty = new byte[32];
    private const uint CoinbaseIndex = uint.MaxValue;
    private const uint CoinbaseSequence = uint.MaxValue;
    private const uint TxInputCount = 1;
    private const uint TxLockTime = 0;

    public static byte[] SerializeCoinbaseTransaction(Script scriptSig, byte[] txOutBytes, uint txVersion,
        uint txVersionGroupId, bool isOverwinterActive, bool isSaplingActive, uint txExpiryHeight, long txBalance,
        uint txVShieldedSpend, uint txVShieldedOutput, uint txJoinSplits)
    {
        using var stream = new MemoryStream();
        var bs = new BitcoinStream(stream, true);

        var txInputCount = TxInputCount;
        bs.ReadWriteAsVarInt(ref txInputCount);
        bs.ReadWrite(Sha256Empty);

        var coinbaseIndex = CoinbaseIndex;
        bs.ReadWrite(ref coinbaseIndex);
        bs.ReadWrite(ref scriptSig);

        var coinbaseSequence = CoinbaseSequence;
        bs.ReadWrite(ref coinbaseSequence);
        bs.ReadWrite(txOutBytes);

        var txLockTime = TxLockTime;
        bs.ReadWrite(ref txLockTime);

        if(isOverwinterActive || isSaplingActive)
            bs.ReadWrite(ref txExpiryHeight);

        if(isSaplingActive)
        {
            bs.ReadWrite(ref txBalance);
            bs.ReadWriteAsVarInt(ref txVShieldedSpend);
            bs.ReadWriteAsVarInt(ref txVShieldedOutput);
        }

        if(isOverwinterActive || isSaplingActive)
            bs.ReadWriteAsVarInt(ref txJoinSplits);

        return stream.ToArray();
    }
}
