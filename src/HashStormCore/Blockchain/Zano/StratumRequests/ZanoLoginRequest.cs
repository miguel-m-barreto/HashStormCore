using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Zano.StratumRequests;

public class ZanoLoginRequest
{
    [JsonProperty("login")]
    public string Login { get; set; }

    [JsonProperty("pass")]
    public string Password { get; set; }

    [JsonProperty("agent")]
    public string UserAgent { get; set; }
}
