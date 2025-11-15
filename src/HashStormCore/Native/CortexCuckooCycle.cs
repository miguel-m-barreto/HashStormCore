using System.Diagnostics;
using System.Runtime.InteropServices;
using HashStormCore.Contracts;
using HashStormCore.Extensions;
using HashStormCore.Messaging;
using HashStormCore.Native;
using HashStormCore.Notifications.Messages;
using CC = HashStormCore.Blockchain.Ethereum.CortexConstants;

// ReSharper disable InconsistentNaming

namespace HashStormCore.Native;

public unsafe class CortexCuckooCycle
{
    internal static IMessageBus messageBus;

    [DllImport("libcortexcuckoocycle", EntryPoint = "cortexcuckoocycle_export", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cortexcuckoocycle(byte* header, int inputLength, uint* solution);

    public int Verify(ReadOnlySpan<byte> data, ReadOnlySpan<uint> result)
    {
        Contract.Requires<ArgumentException>(result.Length == CC.CuckarooSolutionSize);

        var sw = Stopwatch.StartNew();

        fixed (byte* header = data)
        {
            fixed (uint* solution = result)
            {
                var res = cortexcuckoocycle(header, data.Length, solution);

                messageBus?.SendTelemetry("CortexCuckooCycle", TelemetryCategory.Hash, sw.Elapsed, true);

                return res;
            }
        }
    }
}