using HashStormCore.Persistence.Model;

namespace HashStormCore.Payments;

public record PayoutExecutionRunnerRequest
{
    public string PoolId { get; init; }
    public int MaxAttempts { get; init; }
    public DateTime Started { get; init; }
}

public record PayoutExecutionRunnerResult
{
    public int CandidateAttemptCount { get; init; }
    public int ExecutedAttemptCount { get; init; }
    public int SkippedAttemptCount { get; init; }
    public int FailureCount { get; init; }
    public IReadOnlyCollection<PayoutSendExecutionResult> ExecutionResults { get; init; } =
        Array.Empty<PayoutSendExecutionResult>();
    public IReadOnlyCollection<PayoutExecutionSkippedAttempt> SkippedAttempts { get; init; } =
        Array.Empty<PayoutExecutionSkippedAttempt>();
    public IReadOnlyCollection<PayoutExecutionAttemptFailure> Failures { get; init; } =
        Array.Empty<PayoutExecutionAttemptFailure>();
}

public record PayoutExecutionSkippedAttempt
{
    public long AttemptId { get; init; }
    public long BatchId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string Method { get; init; }
    public string Reason { get; init; }
}

public record PayoutExecutionAttemptFailure
{
    public long AttemptId { get; init; }
    public long BatchId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string Method { get; init; }
    public string ErrorType { get; init; }
}
