using Newtonsoft.Json;

namespace HashStormCore.Blockchain.Satoshicash.DaemonResponses;

public class RandomXSCashExtra
{
    [JsonProperty("rx_epoch_duration")]
    public int EpochDurationRandomXSCash { get; set; } = 604800;
}
