using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Beam.DaemonRequests;

public class ValidateAddressRequest
{
    [JsonProperty("address")]
    public string Address { get; set; }
}