namespace HashStormCore.Contracts.Eventing;

public enum ShareEventType
{
    ShareAccepted = 1,
    ShareRejected = 2,
    ShareStale = 3,
    BlockCandidate = 4,
    BlockAccepted = 5,
    BlockRejected = 6,
    NewTemplate = 7,
    PoolOnline = 8,
    PoolOffline = 9
}
