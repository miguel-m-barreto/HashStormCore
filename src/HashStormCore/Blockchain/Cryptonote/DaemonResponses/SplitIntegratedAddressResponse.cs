using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Cryptonote.DaemonResponses;

public class SplitIntegratedAddressResponse
{
    [JsonProperty("standard_address")]
    public string StandardAddress { get; set; }

    public string Payment { get; set; }
}
