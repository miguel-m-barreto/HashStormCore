namespace HashStormCore.Payouts.Adapters;

public enum PayoutSendResultStatus
{
    Accepted,
    FailedPreAccept,
    AmbiguousRequiresReview
}

public record PayoutSendResult
{
    public PayoutSendResultStatus Status { get; init; }
    public IReadOnlyCollection<SettlementEvidence> Evidence { get; init; } = Array.Empty<SettlementEvidence>();
    public string ErrorCode { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
}
