using HashStormCore.Contracts.Eventing;

namespace HashStormCore.Eventing.Abstractions;

public interface IShareEventQueue
{
    int Count { get; }
    long ApproximateBytes { get; }
    bool IsAboveSoftThreshold { get; }
    bool IsAboveCriticalThreshold { get; }
    ValueTask EnqueueAsync(ShareEvent shareEvent, CancellationToken ct);
    ValueTask<ShareEvent> DequeueAsync(CancellationToken ct);
    bool TryDequeue(out ShareEvent shareEvent);
}
