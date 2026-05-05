using HashStormCore.Contracts.Eventing;

namespace HashStormCore.LiveAggregator.Services;

public class LiveWindowRebuilder
{
    public LiveWindowSnapshot Rebuild(IEnumerable<ShareEvent> events, DateTime nowUtc, int windowSeconds)
    {
        var from = nowUtc.ToUniversalTime().AddSeconds(-Math.Max(1, windowSeconds));
        var accepted = events
            .Where(x => x.Created.ToUniversalTime() >= from)
            .Where(x => x.EventType is ShareEventType.ShareAccepted or ShareEventType.BlockCandidate or ShareEventType.BlockAccepted)
            .ToArray();

        var earliest = accepted.Length == 0 ? nowUtc : accepted.Min(x => x.Created.ToUniversalTime());
        var availableWindow = accepted.Length == 0 ? 0 : (int)Math.Min(windowSeconds, Math.Max(1, (nowUtc.ToUniversalTime() - earliest).TotalSeconds));

        return new LiveWindowSnapshot
        {
            Events = accepted,
            WindowSeconds = windowSeconds,
            AvailableWindowSeconds = availableWindow,
            IsWarmingUp = availableWindow < windowSeconds
        };
    }
}

public class LiveWindowSnapshot
{
    public IReadOnlyList<ShareEvent> Events { get; set; } = Array.Empty<ShareEvent>();
    public int WindowSeconds { get; set; }
    public int AvailableWindowSeconds { get; set; }
    public bool IsWarmingUp { get; set; }
}
