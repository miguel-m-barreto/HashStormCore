using HashStormCore.Extensions;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class PayoutSendExecutorService
{
    public PayoutSendExecutorService(IConnectionFactory cf, IPayoutIntentRepository payoutIntentRepository)
    {
        this.cf = cf ?? throw new ArgumentNullException(nameof(cf));
        this.payoutIntentRepository = payoutIntentRepository ?? throw new ArgumentNullException(nameof(payoutIntentRepository));
    }

    private readonly IConnectionFactory cf;
    private readonly IPayoutIntentRepository payoutIntentRepository;
    private const string InvalidResultErrorCode = "sender_invalid_result_ambiguous";
    private const string InvalidEvidenceErrorCode = "sender_invalid_evidence_ambiguous";
    private const string SenderExceptionErrorCode = "sender_exception_ambiguous";

    public async Task<PayoutSendExecutionResult> ExecutePreparedAttemptAsync(PayoutSendExecutionRequest request,
        IPayoutAttemptSender sender, CancellationToken ct)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(sender == null)
            throw new ArgumentNullException(nameof(sender));

        RequireText(request.PoolId, nameof(request.PoolId));

        if(request.AttemptId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.AttemptId), "Payout send attempt id must be greater than zero");

        var claim = await ClaimAttemptAsync(request, ct);
        if(claim.Result != null)
            return claim.Result;

        var context = claim.Context ?? throw new InvalidOperationException("Payout send attempt claim did not return an execution context");

        PayoutAttemptSendResult sendResult;

        try
        {
            sendResult = await sender.SendAsync(context, ct);
        }
        catch(Exception ex)
        {
            return await PersistSenderExceptionAsAmbiguousAsync(request, ex);
        }

        return await PersistSendResultAsync(request, context, sendResult);
    }

    private async Task<ClaimAttemptResult> ClaimAttemptAsync(PayoutSendExecutionRequest request, CancellationToken ct)
    {
        return await cf.RunTx(async (con, tx) =>
        {
            var attempt = await payoutIntentRepository.GetSendAttemptForExecutionAsync(con, tx, request.AttemptId,
                request.PoolId, ct);

            if(attempt == null)
                return ClaimAttemptResult.FromResult(PayoutSendExecutionResult.AttemptNotFound(request.AttemptId));

            if(attempt.State != PayoutSendAttemptStates.Prepared)
                return ClaimAttemptResult.FromResult(PayoutSendExecutionResult.AttemptNotPrepared(request.AttemptId));

            var markedSending = await payoutIntentRepository.MarkAttemptSendingAsync(con, tx, request.AttemptId,
                request.PoolId, request.Started, ct);

            if(!markedSending)
                throw new InvalidOperationException("Prepared payout send attempt could not be transitioned to sending");

            var context = await payoutIntentRepository.GetAttemptExecutionContextAsync(con, tx, request.AttemptId,
                request.PoolId, ct);

            if(context == null)
                throw new InvalidOperationException("Sending payout attempt execution context could not be loaded");

            if(context.Attempt.State != PayoutSendAttemptStates.Sending)
                throw new InvalidOperationException("Payout send attempt execution context was not in sending state");

            if(context.Intents == null || context.Intents.Count == 0)
                throw new InvalidOperationException("Payout send attempt execution context has no mapped intents");

            return ClaimAttemptResult.FromContext(context);
        });
    }

    private async Task<PayoutSendExecutionResult> PersistSendResultAsync(PayoutSendExecutionRequest request,
        PayoutSendExecutionContext context, PayoutAttemptSendResult sendResult)
    {
        if(sendResult == null)
            return await PersistAmbiguousAsync(request, InvalidResultErrorCode, "Sender returned a null payout send result",
                PayoutSendExecutionStatus.SenderFailedAmbiguous, CancellationToken.None);

        switch(sendResult.Status)
        {
            case PayoutAttemptSendStatus.Accepted:
                if(!TryValidateEvidence(sendResult.Evidence, out var evidenceErrorMessage))
                    return await PersistAmbiguousAsync(request, InvalidEvidenceErrorCode, evidenceErrorMessage,
                        PayoutSendExecutionStatus.SenderFailedAmbiguous, CancellationToken.None);

                return await PersistAcceptedAsync(request, context, sendResult, CancellationToken.None);

            case PayoutAttemptSendStatus.FailedPreAccept:
                if(!TryValidateError(sendResult.ErrorCode, sendResult.ErrorMessage))
                    return await PersistAmbiguousAsync(request, InvalidResultErrorCode,
                        "Sender returned failed_pre_accept without a valid error code",
                        PayoutSendExecutionStatus.SenderFailedAmbiguous, CancellationToken.None);

                return await PersistFailedPreAcceptAsync(request, sendResult, CancellationToken.None);

            case PayoutAttemptSendStatus.AmbiguousRequiresReview:
                if(!TryValidateError(sendResult.ErrorCode, sendResult.ErrorMessage))
                    return await PersistAmbiguousAsync(request, InvalidResultErrorCode,
                        "Sender returned ambiguous_requires_review without a valid error code",
                        PayoutSendExecutionStatus.SenderFailedAmbiguous, CancellationToken.None);

                return await PersistAmbiguousAsync(request, sendResult.ErrorCode, sendResult.ErrorMessage,
                    PayoutSendExecutionStatus.AmbiguousRequiresReview, CancellationToken.None);

            default:
                return await PersistAmbiguousAsync(request, InvalidResultErrorCode,
                    "Sender returned an unsupported payout send result status",
                    PayoutSendExecutionStatus.SenderFailedAmbiguous, CancellationToken.None);
        }
    }

    private async Task<PayoutSendExecutionResult> PersistAcceptedAsync(PayoutSendExecutionRequest request,
        PayoutSendExecutionContext context, PayoutAttemptSendResult sendResult, CancellationToken ct)
    {
        return await cf.RunTx(async (con, tx) =>
        {
            var accepted = await payoutIntentRepository.MarkAttemptAcceptedAsync(con, tx, request.AttemptId,
                request.PoolId, sendResult.Evidence, request.Started, ct);

            if(!accepted)
                throw new InvalidOperationException("Sending payout send attempt could not be marked accepted");

            foreach(var evidence in sendResult.AdditionalEvidence ?? Array.Empty<PayoutAttemptEvidence>())
            {
                ValidateEvidence(evidence);

                await payoutIntentRepository.InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
                {
                    PoolId = context.Attempt.PoolId,
                    Coin = context.Attempt.Coin,
                    BatchId = context.Attempt.BatchId,
                    AttemptId = context.Attempt.Id,
                    Kind = evidence.Kind,
                    Value = evidence.Value,
                    Created = request.Started
                }, ct);
            }

            return PayoutSendExecutionResult.Accepted(request.AttemptId, sendResult.Evidence);
        });
    }

    private async Task<PayoutSendExecutionResult> PersistFailedPreAcceptAsync(PayoutSendExecutionRequest request,
        PayoutAttemptSendResult sendResult, CancellationToken ct)
    {
        return await cf.RunTx(async (con, tx) =>
        {
            var failed = await payoutIntentRepository.MarkAttemptFailedPreAcceptAsync(con, tx, request.AttemptId,
                request.PoolId, sendResult.ErrorCode, sendResult.ErrorMessage, request.Started, ct);

            if(!failed)
                throw new InvalidOperationException("Payout send attempt could not be marked failed_pre_accept");

            return PayoutSendExecutionResult.FailedPreAccept(request.AttemptId, sendResult.ErrorCode,
                sendResult.ErrorMessage);
        });
    }

    private async Task<PayoutSendExecutionResult> PersistAmbiguousAsync(PayoutSendExecutionRequest request,
        string errorCode, string errorMessage, PayoutSendExecutionStatus resultStatus, CancellationToken ct)
    {
        return await cf.RunTx(async (con, tx) =>
        {
            var ambiguous = await payoutIntentRepository.MarkAttemptAmbiguousAsync(con, tx, request.AttemptId,
                request.PoolId, errorCode, errorMessage, request.Started, ct);

            if(!ambiguous)
                throw new InvalidOperationException("Sending payout send attempt could not be marked ambiguous");

            return new PayoutSendExecutionResult
            {
                Status = resultStatus,
                AttemptId = request.AttemptId,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            };
        });
    }

    private Task<PayoutSendExecutionResult> PersistSenderExceptionAsAmbiguousAsync(PayoutSendExecutionRequest request,
        Exception ex)
    {
        var errorMessage = ex.GetType().Name;

        return PersistAmbiguousAsync(request, SenderExceptionErrorCode, errorMessage,
            PayoutSendExecutionStatus.SenderFailedAmbiguous, CancellationToken.None);
    }

    private static void RequireText(string value, string name)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required", name);
    }

#nullable enable annotations
    private static void ValidateEvidence(PayoutAttemptEvidence? evidence)
    {
        if(!TryValidateEvidence(evidence, out var errorMessage))
            throw new ArgumentException(errorMessage, nameof(evidence));
    }

    private static bool TryValidateEvidence(PayoutAttemptEvidence? evidence, out string errorMessage)
    {
        if(evidence == null)
        {
            errorMessage = "Sender accepted result did not include primary external evidence";
            return false;
        }

        if(string.IsNullOrWhiteSpace(evidence.Kind))
        {
            errorMessage = "Sender accepted result included empty primary evidence kind";
            return false;
        }

        if(string.IsNullOrWhiteSpace(evidence.Value))
        {
            errorMessage = "Sender accepted result included empty primary evidence value";
            return false;
        }

        switch(evidence.Kind)
        {
            case PayoutExternalConfirmationKinds.TxId:
            case PayoutExternalConfirmationKinds.OperationId:
            case PayoutExternalConfirmationKinds.WalletAck:
            case PayoutExternalConfirmationKinds.RawHash:
                errorMessage = string.Empty;
                return true;

            default:
                errorMessage = "Sender accepted result included unsupported primary evidence kind";
                return false;
        }
    }
#nullable restore

    private static void ValidateError(string errorCode, string errorMessage)
    {
        if(!TryValidateError(errorCode, errorMessage))
            throw new ArgumentException("Payout send result requires a non-empty error code", nameof(errorCode));
    }

    private static bool TryValidateError(string errorCode, string errorMessage)
    {
        return !string.IsNullOrWhiteSpace(errorCode) &&
            (errorMessage == null || !string.IsNullOrWhiteSpace(errorMessage));
    }

#nullable enable annotations
    private record ClaimAttemptResult
    {
        public PayoutSendExecutionResult? Result { get; init; }
        public PayoutSendExecutionContext? Context { get; init; }

        public static ClaimAttemptResult FromResult(PayoutSendExecutionResult result)
        {
            return new ClaimAttemptResult
            {
                Result = result
            };
        }

        public static ClaimAttemptResult FromContext(PayoutSendExecutionContext context)
        {
            return new ClaimAttemptResult
            {
                Context = context
            };
        }
    }
#nullable restore
}
