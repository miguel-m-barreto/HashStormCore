using HashStormCore.Contracts;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HashStormCore.Extensions;
using HashStormCore.Messaging;
using HashStormCore.Native;
using HashStormCore.Notifications.Messages;

// ReSharper disable InconsistentNaming

namespace HashStormCore.Native;

public unsafe class AstroBWTv3
{
    internal static IMessageBus messageBus;

    [DllImport("libdero", EntryPoint = "astroBWTv3_export", CallingConvention = CallingConvention.Cdecl)]
    public static extern void astroBWTv3(byte* input, int inputLength, void* output);

    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        var sw = Stopwatch.StartNew();

        fixed (byte* input = data)
        {
            fixed (byte* output = result)
            {
                astroBWTv3(input, data.Length, output);

                messageBus?.SendTelemetry("AstroBWTv3", TelemetryCategory.Hash, sw.Elapsed, true);
            }
        }
    }
}