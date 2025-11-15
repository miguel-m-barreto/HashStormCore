using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Conceal.StratumRequests;

public class ConcealGetJobRequest
{
    [JsonProperty("id")]
    public string WorkerId { get; set; }
}