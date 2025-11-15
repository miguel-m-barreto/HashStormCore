using HashStormCore.Mining;

namespace HashStormCore.Notifications.Messages;

public enum PoolStatus
{
    Online,
    Offline
}

public record PoolStatusNotification
{
    public IMiningPool Pool { get; set; }
    public PoolStatus Status { get; set; }
}
