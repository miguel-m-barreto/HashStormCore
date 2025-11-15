using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Conceal.DaemonResponses;

public class SplitIntegratedAddressResponse
{
    [JsonProperty("address")]
    public string StandardAddress { get; set; }
    
    [JsonProperty("payment_id")]
    public string Payment { get; set; }
}