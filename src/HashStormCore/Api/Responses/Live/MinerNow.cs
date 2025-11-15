// Responses/Live/MinerNow.cs
using System;

namespace HashStormCore.Api.Responses.Live;

public class MinerNow
{
    public string Address { get; set; }
    public double Hashrate { get; set; }
    public bool Online { get; set; }
    public DateTime? LastShareAt { get; set; }
}
