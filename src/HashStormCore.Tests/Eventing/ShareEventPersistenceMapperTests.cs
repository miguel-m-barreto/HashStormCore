using HashStormCore.Contracts.Eventing;
using HashStormCore.DbWriter.Services;
using Xunit;

namespace HashStormCore.Tests.Eventing;

public class ShareEventPersistenceMapperTests
{
    [Theory]
    [InlineData(ShareEventType.ShareAccepted, true)]
    [InlineData(ShareEventType.BlockCandidate, true)]
    [InlineData(ShareEventType.BlockAccepted, true)]
    [InlineData(ShareEventType.ShareRejected, false)]
    [InlineData(ShareEventType.ShareStale, false)]
    [InlineData(ShareEventType.BlockRejected, false)]
    [InlineData(ShareEventType.NewTemplate, false)]
    public void OnlyPersistibleShareEventsGoToAccountingShares(ShareEventType eventType, bool expected)
    {
        var mapper = new ShareEventPersistenceMapper();

        Assert.Equal(expected, mapper.IsPersistibleShare(new ShareEvent { EventType = eventType }));
    }
}
