using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Beam.DaemonResponses;

public class GetBlockResponse
{
    public string Chainwork { get; set; }
    public double Difficulty { get; set; }
    public string Hash { get; set; }
    public ulong Height { get; set; }
    public ulong Subsidy { get; set; }
    public ulong Timestamp { get; set; }
}