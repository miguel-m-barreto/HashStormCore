namespace HashStormCore.Payouts.Profiles;

public enum PayoutProfileResolutionStatus
{
    Resolved,
    Unsupported,
    NotReady
}

public record PayoutProfileResolution
{
    public PayoutProfileResolutionStatus Status { get; init; }
    public PayoutProfile Profile { get; init; }
    public string Reason { get; init; } = string.Empty;

    public bool HasProfile => Profile != null;

    public static PayoutProfileResolution Resolved(PayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new PayoutProfileResolution
        {
            Status = profile.ReservationReady ? PayoutProfileResolutionStatus.Resolved : PayoutProfileResolutionStatus.NotReady,
            Profile = profile,
            Reason = profile.NotReadyReason
        };
    }

    public static PayoutProfileResolution NotReady(PayoutProfile profile, string reason)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new PayoutProfileResolution
        {
            Status = PayoutProfileResolutionStatus.NotReady,
            Profile = profile with
            {
                ReservationReady = false,
                NotReadyReason = reason
            },
            Reason = reason
        };
    }

    public static PayoutProfileResolution Unsupported(string reason)
    {
        return new PayoutProfileResolution
        {
            Status = PayoutProfileResolutionStatus.Unsupported,
            Reason = reason
        };
    }
}
