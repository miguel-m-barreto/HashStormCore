using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Conceal.DaemonResponses;

public class GetAddressResponse
{
    [JsonProperty("addresses")]
    public string[] Address { get; set; }
}