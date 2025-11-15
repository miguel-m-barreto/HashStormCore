using HashStormCore.Configuration;
using Newtonsoft.Json.Linq;

namespace HashStormCore.Blockchain.Satoshicash.Configuration;

public class SatoshicashPoolConfigExtra : Bitcoin.Configuration.BitcoinPoolConfigExtra
{
    /// <summary>
    /// RandomXSCash virtual machine bucket
    /// Defaults to poolId if not specified
    /// </summary>
    public string RandomXRealm { get; set; }

    /// <summary>
    /// Optional override value for RandomXSCash VM Flags (see Native/LibRandomX.cs)
    /// </summary>
    public JToken RandomXFlagsOverride { get; set; }

    /// <summary>
    /// Optional additive value for RandomXSCash VM Flags (see Native/LibRandomX.cs)
    /// </summary>
    public JToken RandomXFlagsAdd { get; set; }

    /// <summary>
    /// Optional value for number of RandomXSCash VMs allocated per generation (new seed hash)
    /// Set to -1 to scale to number of cores
    /// Default: 2 (https://github.com/scashnetwork/sips/blob/main/scash-protocol-spec.md#4-algorithm-performance)
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public int RandomXVmCount { get; set; } = 2;
}
