using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Xelis.DaemonRequests;

public class SubmitBlockRequest
{
    [JsonProperty("block_template")]
    public string BlockTemplate { get; set; }

    [JsonProperty("miner_work", NullValueHandling = NullValueHandling.Ignore)]
    public string MinerWork { get; set; }
}
