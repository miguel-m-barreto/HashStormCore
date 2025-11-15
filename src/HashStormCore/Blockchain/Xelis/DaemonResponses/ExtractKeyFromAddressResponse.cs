using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Xelis.DaemonResponses;

public class ExtractKeyFromAddressResponse
{
    [JsonProperty("hex")]
    public string PublicKey { get; set; }
}
