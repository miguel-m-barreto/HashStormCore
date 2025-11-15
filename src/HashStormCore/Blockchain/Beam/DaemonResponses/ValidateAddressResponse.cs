using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Beam.DaemonResponses;

public class ValidateAddressResponse
{
    [JsonProperty("is_valid")]
    public bool IsValid { get; set; }
    [JsonProperty("is_mine")]
    public bool IsMine { get; set; }
    public string Type { get; set; } = null;
    public int Payments { get; set; } = 0;
}