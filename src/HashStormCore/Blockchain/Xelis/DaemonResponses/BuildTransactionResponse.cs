using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Xelis.DaemonResponses;

public class BuildTransactionResponse
{
    public string Hash { get; set; }
    public ulong Fee  { get; set; }

    [JsonProperty("tx_as_hex")]
    public string TxHash { get; set; }
}
