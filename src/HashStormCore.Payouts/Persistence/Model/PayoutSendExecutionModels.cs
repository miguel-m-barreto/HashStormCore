namespace HashStormCore.Persistence.Model;

public record PayoutSendExecutionContext
{
    public PayoutBatch Batch { get; init; }
    public PayoutSendAttempt Attempt { get; init; }
    public IReadOnlyCollection<PayoutSendExecutionIntent> Intents { get; init; } = Array.Empty<PayoutSendExecutionIntent>();
}

public record PayoutSendExecutionIntent
{
    public long IntentId { get; init; }
    public long AttemptId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string Address { get; init; }
    public decimal Amount { get; init; }
    public string IntentState { get; init; }
    public string AttemptIntentState { get; init; }
}

public record PayoutSendExecutionRequest
{
    public string PoolId { get; init; }
    public long AttemptId { get; init; }
    public DateTime Started { get; init; }
}

public enum PayoutSendExecutionStatus
{
    Accepted,
    FailedPreAccept,
    AmbiguousRequiresReview,
    SenderFailedAmbiguous,
    AttemptNotFound,
    AttemptNotPrepared
}

#nullable enable annotations
public record PayoutSendExecutionResult
{
    public PayoutSendExecutionStatus Status { get; init; }
    public long AttemptId { get; init; }
    public PayoutAttemptEvidence? Evidence { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static PayoutSendExecutionResult Accepted(long attemptId, PayoutAttemptEvidence evidence)
    {
        return new PayoutSendExecutionResult
        {
            Status = PayoutSendExecutionStatus.Accepted,
            AttemptId = attemptId,
            Evidence = evidence
        };
    }

    public static PayoutSendExecutionResult FailedPreAccept(long attemptId, string errorCode, string? errorMessage)
    {
        return new PayoutSendExecutionResult
        {
            Status = PayoutSendExecutionStatus.FailedPreAccept,
            AttemptId = attemptId,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    public static PayoutSendExecutionResult AmbiguousRequiresReview(long attemptId, string errorCode, string? errorMessage)
    {
        return new PayoutSendExecutionResult
        {
            Status = PayoutSendExecutionStatus.AmbiguousRequiresReview,
            AttemptId = attemptId,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    public static PayoutSendExecutionResult SenderFailedAmbiguous(long attemptId, string errorCode, string? errorMessage)
    {
        return new PayoutSendExecutionResult
        {
            Status = PayoutSendExecutionStatus.SenderFailedAmbiguous,
            AttemptId = attemptId,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    public static PayoutSendExecutionResult AttemptNotFound(long attemptId)
    {
        return new PayoutSendExecutionResult
        {
            Status = PayoutSendExecutionStatus.AttemptNotFound,
            AttemptId = attemptId
        };
    }

    public static PayoutSendExecutionResult AttemptNotPrepared(long attemptId)
    {
        return new PayoutSendExecutionResult
        {
            Status = PayoutSendExecutionStatus.AttemptNotPrepared,
            AttemptId = attemptId
        };
    }
}

public enum PayoutAttemptSendStatus
{
    Accepted,
    FailedPreAccept,
    AmbiguousRequiresReview
}

public record PayoutAttemptSendResult
{
    public PayoutAttemptSendStatus Status { get; init; }
    public PayoutAttemptEvidence? Evidence { get; init; }
    public IReadOnlyCollection<PayoutAttemptEvidence> AdditionalEvidence { get; init; } = Array.Empty<PayoutAttemptEvidence>();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static PayoutAttemptSendResult Accepted(PayoutAttemptEvidence evidence)
    {
        return Accepted(evidence, Array.Empty<PayoutAttemptEvidence>());
    }

    public static PayoutAttemptSendResult Accepted(PayoutAttemptEvidence evidence,
        IReadOnlyCollection<PayoutAttemptEvidence> additionalEvidence)
    {
        return new PayoutAttemptSendResult
        {
            Status = PayoutAttemptSendStatus.Accepted,
            Evidence = evidence,
            AdditionalEvidence = additionalEvidence ?? Array.Empty<PayoutAttemptEvidence>()
        };
    }

    public static PayoutAttemptSendResult FailedPreAccept(string errorCode, string? errorMessage)
    {
        return new PayoutAttemptSendResult
        {
            Status = PayoutAttemptSendStatus.FailedPreAccept,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    public static PayoutAttemptSendResult Ambiguous(string errorCode, string? errorMessage)
    {
        return new PayoutAttemptSendResult
        {
            Status = PayoutAttemptSendStatus.AmbiguousRequiresReview,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }
}
#nullable restore
