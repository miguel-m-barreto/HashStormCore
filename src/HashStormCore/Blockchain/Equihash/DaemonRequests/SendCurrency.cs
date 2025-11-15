using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Equihash.DaemonRequests;

public class SendCurrencyOutputs
{
    [JsonProperty("currency", NullValueHandling = NullValueHandling.Ignore)]
    public string Currency { get; set; }
    
    public decimal Amount { get; set; }
    public string Address { get; set; }
}