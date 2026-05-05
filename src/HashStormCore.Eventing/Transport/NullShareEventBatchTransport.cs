using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Abstractions;
using HashStormCore.Eventing.Publishing;

namespace HashStormCore.Eventing.Transport;

public class NullShareEventBatchTransport : IShareEventBatchTransport
{
    public Task<ShareBatchPublishResult> PublishAsync(ShareEventBatch batch, CancellationToken ct)
    {
        throw new InvalidOperationException("Null share event batch transport cannot publish events. Disable the publisher or configure a supported eventPipeline.broker.type.");
    }
}
