using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Xelis.DaemonRequests;

public class GetBlockTemplateRequest
{
    [JsonProperty("template")]
    public string BlockHeader { get; set; }

    public string Address { get; set; }
}
