using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Zano.StratumRequests;

public class ZanoGetJobRequest
{
    [JsonProperty("id")]
    public string WorkerId { get; set; }
}
