using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Xelis.DaemonRequests;

public class GetBlockByHashRequest
{
    public string Hash { get; set; }

    [JsonProperty("include_txs")]
    public bool IncludeTransactions { get; set; } = true;
}
