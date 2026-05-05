using HashStormCore.Contracts.Eventing;

namespace HashStormCore.Eventing.Mapping;

public static class ShareEventMapper
{
    public static ShareEvent Map(ShareEventSource source, ShareEventType eventType = ShareEventType.ShareAccepted,
        string rejectReason = "", string errorCode = "", string errorMessage = "")
    {
        var normalizedType = source.IsBlockCandidate && eventType == ShareEventType.ShareAccepted
            ? ShareEventType.BlockCandidate
            : eventType;

        var created = source.Created == default ? DateTime.UtcNow : source.Created.ToUniversalTime();

        var result = new ShareEvent
        {
            EventType = normalizedType,
            PoolId = source.PoolId ?? string.Empty,
            CoinSymbol = source.CoinSymbol ?? string.Empty,
            CoinFamily = source.CoinFamily ?? string.Empty,
            Miner = source.Miner ?? string.Empty,
            Worker = source.Worker ?? string.Empty,
            Source = source.Source ?? string.Empty,
            Created = created,
            BlockHeight = source.BlockHeight,
            Difficulty = source.Difficulty,
            NetworkDifficulty = source.NetworkDifficulty,
            ShareMultiplier = source.ShareMultiplier,
            IsBlockCandidate = source.IsBlockCandidate,
            BlockHash = source.BlockHash ?? string.Empty,
            IpAddress = source.IpAddress ?? string.Empty,
            UserAgent = source.UserAgent ?? string.Empty,
            RejectReason = rejectReason ?? string.Empty,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
            TransactionConfirmationData = source.TransactionConfirmationData ?? string.Empty,
            BlockReward = source.BlockReward,
            BlockType = source.BlockType ?? string.Empty
        };

        result.EventId = Guid.NewGuid().ToString("N");
        return result;
    }
}
