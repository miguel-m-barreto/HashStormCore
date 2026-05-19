using System.Data;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Model.Projections;

namespace HashStormCore.Persistence.Repositories;

public interface IPayoutIntentRepository
{
    Task<PayoutBatch> CreateReservedBatchAsync(IDbConnection con, IDbTransaction tx, CreatePayoutBatchRequest batch,
        IReadOnlyCollection<CreatePayoutIntentRequest> intents, CancellationToken ct);

    Task<PayoutSendAttempt> CreateSendAttemptAsync(IDbConnection con, IDbTransaction tx, CreatePayoutSendAttemptRequest attempt,
        IReadOnlyCollection<long> intentIds, CancellationToken ct);

    Task<PayoutExternalConfirmation> InsertExternalConfirmationAsync(IDbConnection con, IDbTransaction tx,
        PayoutExternalConfirmation confirmation, CancellationToken ct);

    Task<bool> MarkAttemptSendingAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId, DateTime updated, CancellationToken ct);
    Task<bool> MarkAttemptAcceptedAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId, PayoutAttemptEvidence evidence, DateTime updated, CancellationToken ct);
    Task<bool> MarkAmbiguousAttemptAcceptedAfterReviewAsync(IDbConnection con, IDbTransaction tx, long batchId,
        long attemptId, string poolId, PayoutAttemptEvidence evidence, DateTime updated, CancellationToken ct);
    Task<bool> MarkAttemptAmbiguousAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId, string errorCode, string errorMessage, DateTime updated, CancellationToken ct);
    Task<bool> MarkStaleBatchAmbiguousAsync(IDbConnection con, IDbTransaction tx, long batchId, long staleAttemptId,
        string poolId, string errorCode, string errorMessage, DateTime updated, CancellationToken ct);
    Task<bool> MarkAttemptFailedPreAcceptAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId, string errorCode, string errorMessage, DateTime updated, CancellationToken ct);
    Task<bool> MarkAttemptFailedNoAcceptAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId, string errorCode, string errorMessage, DateTime updated, CancellationToken ct);
    Task<bool> MarkBatchCancelledAsync(IDbConnection con, IDbTransaction tx, long batchId, string poolId, string errorCode, string errorMessage, DateTime updated, CancellationToken ct);

    Task<PayoutBatch> GetActiveBatchForPoolAsync(IDbConnection con, string poolId, CancellationToken ct);
    Task<PayoutBatch> GetActiveBatchForPoolAsync(IDbConnection con, IDbTransaction tx, string poolId, CancellationToken ct);
    Task<PayoutBatch> GetBatchForUpdateAsync(IDbConnection con, IDbTransaction tx, long batchId, string poolId, string coin, CancellationToken ct);
    Task<PayoutIntent[]> GetReservedIntentsForBatchAsync(IDbConnection con, IDbTransaction tx, long batchId, string poolId, string coin, CancellationToken ct);
    Task<long> GetSendAttemptCountForBatchAsync(IDbConnection con, IDbTransaction tx, long batchId, string poolId, string coin, CancellationToken ct);
    Task<PayoutSendAttempt[]> GetPreparedAttemptsForExecutionAsync(IDbConnection con, IDbTransaction tx, string poolId, int limit, CancellationToken ct);
#nullable enable annotations
    Task<PayoutSendAttempt?> GetSendAttemptForExecutionAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId, CancellationToken ct);
    Task<PayoutSendExecutionContext?> GetAttemptExecutionContextAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId, CancellationToken ct);
#nullable restore
    Task<PayoutReconciliationAttemptSummary[]> GetStaleSendingAttemptsForUpdateAsync(IDbConnection con, IDbTransaction tx,
        string poolId, DateTime olderThan, int limit, CancellationToken ct);
    Task<PayoutStaleSendingBatchCandidate[]> GetStaleSendingBatchesForUpdateAsync(IDbConnection con, IDbTransaction tx,
        string poolId, DateTime olderThan, int limit, CancellationToken ct);
    Task<PayoutReconciliationAttemptSummary[]> GetAmbiguousAttemptsAsync(IDbConnection con, IDbTransaction tx,
        string poolId, int limit, CancellationToken ct);
    Task<PayoutReconciliationAttemptSummary[]> GetAttemptsWithOperationIdAsync(IDbConnection con, IDbTransaction tx,
        string poolId, int limit, CancellationToken ct);
    Task<PayoutAttemptConfirmationSummary[]> GetAttemptConfirmationsAsync(IDbConnection con, IDbTransaction tx,
        long batchId, long attemptId, string poolId, CancellationToken ct);
    Task<PayoutBatch[]> GetRecoverableBatchesAsync(IDbConnection con, string poolId, CancellationToken ct);
    Task<PayoutSendAttempt[]> GetStaleSendingAttemptsAsync(IDbConnection con, DateTime before, int limit, CancellationToken ct);
    Task<PayoutBalanceProjection[]> GetBalanceProjectionsAsync(IDbConnection con, string poolId, CancellationToken ct);
}
