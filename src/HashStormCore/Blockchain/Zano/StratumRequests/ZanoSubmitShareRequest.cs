using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Zano.StratumRequests;

public class ZanoSubmitShareRequest
{
    [JsonProperty("job_id")]
    public string JobId { get; set; }

    public string Nonce { get; set; }

    [JsonProperty("result")]
    public string Hash { get; set; }
}
