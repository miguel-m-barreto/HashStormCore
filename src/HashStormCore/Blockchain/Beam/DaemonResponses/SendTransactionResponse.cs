using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Beam.DaemonResponses;

public class SendTransactionResponse
{
    [JsonProperty("txId")]
    public string TxId { get; set; }
}