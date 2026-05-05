using HashStormCore.Contracts.Eventing;

namespace HashStormCore.DbWriter.Services;

public class ShareEventPersistenceMapper
{
    public bool IsPersistibleShare(ShareEvent shareEvent)
    {
        return shareEvent.EventType is ShareEventType.ShareAccepted or
            ShareEventType.BlockCandidate or
            ShareEventType.BlockAccepted;
    }

    public PersistedShare Map(ShareEvent shareEvent)
    {
        return new PersistedShare
        {
            PoolId = shareEvent.PoolId,
            BlockHeight = shareEvent.BlockHeight ?? 0,
            Difficulty = shareEvent.Difficulty,
            NetworkDifficulty = shareEvent.NetworkDifficulty,
            Miner = shareEvent.Miner,
            Worker = shareEvent.Worker,
            UserAgent = shareEvent.UserAgent,
            IpAddress = shareEvent.IpAddress,
            Source = shareEvent.Source,
            Created = shareEvent.Created == default ? DateTime.UtcNow : shareEvent.Created
        };
    }
}

public class PersistedShare
{
    public string PoolId { get; set; } = string.Empty;
    public long BlockHeight { get; set; }
    public double Difficulty { get; set; }
    public double NetworkDifficulty { get; set; }
    public string Miner { get; set; } = string.Empty;
    public string Worker { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime Created { get; set; }
}
