using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Beam.DaemonRequests;

public class GetBlockHeaderRequest
{
    [JsonProperty("height")]
    public ulong Height { get; set; }
}