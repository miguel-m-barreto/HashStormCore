using System.Text.Json.Serialization;

namespace HashStormCore.Blockchain.Beam.DaemonResponses;

public record GetStatusResponse
{
    public string Chainwork { get; set; }
    public string Hash { get; set; }
    public ulong Height { get; set; }
    
    [JsonPropertyName("peers_count")]
    public int PeersCount { get; set; }
    
    public ulong Timestamp { get; set; }
}