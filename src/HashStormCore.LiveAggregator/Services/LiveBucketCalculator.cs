using HashStormCore.Contracts.Eventing;

namespace HashStormCore.LiveAggregator.Services;

public class LiveBucketCalculator
{
    public long BucketTimestamp(DateTime created)
    {
        return new DateTimeOffset(created.ToUniversalTime()).ToUnixTimeSeconds();
    }

    public double WeightedDifficulty(ShareEvent shareEvent)
    {
        var multiplier = shareEvent.ShareMultiplier > 0 ? shareEvent.ShareMultiplier : 1d;
        return Math.Max(shareEvent.Difficulty, 0d) * multiplier;
    }
}
