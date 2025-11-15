using System.Text.Json.Serialization;

namespace HashStormCore.Blockchain.Warthog.DaemonResponses;

public class WarthogSendTransactionData
{
    [JsonPropertyName("txHash")]
    public string TxHash { get; set; }
}

public class WarthogSendTransactionResponse : WarthogResponseBase
{
    public WarthogSendTransactionData Data { get; set; }
}
