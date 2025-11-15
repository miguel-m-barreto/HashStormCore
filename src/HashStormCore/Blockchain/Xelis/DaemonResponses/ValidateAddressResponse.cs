using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Xelis.DaemonResponses;

public class ValidateAddressResponse
{
    [JsonProperty("is_integrated")]
    public bool IsIntegrated { get; set; }

    [JsonProperty("is_valid")]
    public bool IsValid { get; set; }
}
