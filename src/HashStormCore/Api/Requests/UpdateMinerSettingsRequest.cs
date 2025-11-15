using HashStormCore.Api.Responses;

namespace HashStormCore.Api.Requests;

public class UpdateMinerSettingsRequest
{
    public string IpAddress { get; set; }
    public MinerSettings Settings { get; set; }
}
