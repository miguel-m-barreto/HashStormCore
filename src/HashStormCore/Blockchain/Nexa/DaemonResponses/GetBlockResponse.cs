using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Nexa.DaemonResponses;

public class Block
{
    [JsonProperty("hash")]
    public string Hash { get; set; }

    [JsonProperty("txid")]
    public string[] Transactions { get; set; }
}
