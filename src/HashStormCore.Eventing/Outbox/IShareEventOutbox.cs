using HashStormCore.Contracts.Eventing;

namespace HashStormCore.Eventing.Outbox;

public interface IShareEventOutbox
{
    Task AppendAsync(IReadOnlyList<ShareEvent> events, bool flushToDisk, CancellationToken ct);
    Task FlushAsync(bool flushToDisk, CancellationToken ct);
    IAsyncEnumerable<ShareEventOutboxRecord> ReadFromCheckpointAsync(CancellationToken ct);
    Task AdvanceCheckpointAsync(ShareEventOutboxRecord record, CancellationToken ct);
}
