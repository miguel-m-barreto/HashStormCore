using System.Diagnostics;
using HashStormCore.Blockchain.Progpow;
using HashStormCore.Contracts;
using HashStormCore.Extensions;
using HashStormCore.Messaging;
using HashStormCore.Native;
using HashStormCore.Notifications.Messages;
using NLog;

namespace HashStormCore.Crypto.Hashing.Progpow.Sccpow;

[Identifier("sccpow")]
public class Cache : IProgpowCache
{
    public Cache(int epoch)
    {
        Epoch = epoch;
        LastUsed = DateTime.Now;
    }

    private IntPtr handle = IntPtr.Zero;
    private bool isGenerated = false;
    private readonly object genLock = new();
    internal static IMessageBus messageBus;
    public int Epoch { get; }
    public byte[] SeedHash { get; set; }
    public DateTime LastUsed { get; set; }

    public void Dispose()
    {
        if(handle != IntPtr.Zero)
        {
            SccPow.DestroyContext(handle);
            handle = IntPtr.Zero;
        }
    }

    public async Task GenerateAsync(ILogger logger, CancellationToken ct)
    {
        if(handle == IntPtr.Zero)
        {
            await Task.Run(() =>
            {
                lock(genLock)
                {
                    if(!isGenerated)
                    {
                        // re-check after obtaining lock
                        if(handle != IntPtr.Zero)
                            return;

                        var started = DateTime.Now;
                        logger.Debug(() => $"Generating cache for epoch {Epoch}");

                        handle = SccPow.CreateContext(Epoch);

                        logger.Debug(() => $"Done generating cache for epoch {Epoch} after {DateTime.Now - started}");

                        // get the seed hash for this epoch
                        var res = SccPow.calculate_epoch_seed(Epoch);
                        SeedHash = res.bytes;
                        logger.Info(() => $"Seed hash for epoch {Epoch} is {SeedHash.ToHexString()}");

                        isGenerated = true;
                    }
                }
            }, ct);
        }
    }

    public unsafe bool Compute(ILogger logger, int blockNumber, byte[] hash, ulong nonce, out byte[] mixDigest, out byte[] result)
    {
        Contract.RequiresNonNull(hash);

        var sw = Stopwatch.StartNew();

        mixDigest = null;
        result = null;

        var value = new SccPow.Ethash_result();

        var inputHash = new SccPow.Ethash_hash256();
        inputHash.bytes = hash;

        fixed(byte* input = hash)
        {
            value = SccPow.hash(handle, blockNumber, ref inputHash, nonce);
        }

        if(value.final_hash.bytes == null)
        {
            logger.Error(() => $"SccPow.hash returned null");
            return false;
        }

        mixDigest = value.mix_hash.bytes;
        result = value.final_hash.bytes;

        messageBus?.SendTelemetry("Sccpow", TelemetryCategory.Hash, sw.Elapsed, true);

        return true;
    }
}