using HashStormCore.JsonRpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HashStormCore.Blockchain.Equihash.DaemonResponses;

public class ZCashAsyncOperationStatus
{
    [JsonProperty("id")]
    public string OperationId { get; set; }

    public string Status { get; set; }
    public JToken Result { get; set; }
    public JsonRpcError Error { get; set; }
}
