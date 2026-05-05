using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Publishing;

namespace HashStormCore.Eventing.Abstractions;

public interface IShareEventBatchTransport
{
    Task<ShareBatchPublishResult> PublishAsync(ShareEventBatch batch, CancellationToken ct);
}
