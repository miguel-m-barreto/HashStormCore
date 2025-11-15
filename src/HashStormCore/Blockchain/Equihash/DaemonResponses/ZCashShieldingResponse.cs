using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Equihash.DaemonResponses;

public class ZCashShieldingResponse
{
    [JsonProperty("opid")]
    public string OperationId { get; set; }
}
