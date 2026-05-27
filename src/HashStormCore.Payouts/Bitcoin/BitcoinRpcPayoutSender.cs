using System.Collections.ObjectModel;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;

namespace HashStormCore.Payouts.Bitcoin;

public class BitcoinRpcPayoutSender : IPayoutAttemptSender
{
    public BitcoinRpcPayoutSender(IBitcoinPayoutRpcClient rpcClient)
    {
        this.rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
        evidenceValidator = new PayoutExecutionEvidenceValidator();
    }

    public const string InvalidContextErrorCode = "bitcoin_sender_invalid_context";
    public const string UnsupportedShapeErrorCode = "bitcoin_sender_unsupported_shape";
    public const string InvalidRecipientErrorCode = "bitcoin_sender_invalid_recipient";
    public const string InvalidAmountErrorCode = "bitcoin_sender_invalid_amount";
    public const string InvalidRpcResultErrorCode = "bitcoin_sender_invalid_rpc_result";

    private readonly IBitcoinPayoutRpcClient rpcClient;
    private readonly PayoutExecutionEvidenceValidator evidenceValidator;

    public async Task<PayoutAttemptSendResult> SendAsync(PayoutSendExecutionContext context, CancellationToken ct)
    {
        if(context == null)
            throw new ArgumentNullException(nameof(context));

        var contextValidation = ValidateBaseContext(context);
        if(contextValidation != null)
            return contextValidation;

        if(IsSendManyContext(context))
            return MapRpcResult(await rpcClient.SendManyAsync(CreateSendManyRequest(context), ct));

        if(IsSendToAddressContext(context))
        {
            if(context.Intents.Count != 1)
                return PayoutAttemptSendResult.FailedPreAccept(InvalidRecipientErrorCode,
                    "Bitcoin sendtoaddress requires exactly one payout recipient");

            return MapRpcResult(await rpcClient.SendToAddressAsync(CreateSendToAddressRequest(context), ct));
        }

        return PayoutAttemptSendResult.FailedPreAccept(UnsupportedShapeErrorCode,
            "Bitcoin RPC sender supports only bitcoin-rpc sendmany batch attempts and sendtoaddress per-address attempts");
    }

    private PayoutAttemptSendResult ValidateBaseContext(PayoutSendExecutionContext context)
    {
        if(context.Batch == null || context.Attempt == null || context.Intents == null)
            return PayoutAttemptSendResult.FailedPreAccept(InvalidContextErrorCode,
                "Payout send context is missing batch, attempt, or intents");

        if(!string.Equals(context.Batch.Handler, PayoutProfileConstants.AdapterIds.BitcoinRpc, StringComparison.Ordinal))
            return PayoutAttemptSendResult.FailedPreAccept(UnsupportedShapeErrorCode,
                "Bitcoin RPC sender requires bitcoin-rpc batch handler");

        if(!MatchingNonEmptyValues(context.Batch.PoolId, context.Attempt.PoolId) ||
           !MatchingNonEmptyValues(context.Batch.Coin, context.Attempt.Coin))
            return PayoutAttemptSendResult.FailedPreAccept(InvalidContextErrorCode,
                "Bitcoin RPC sender requires matching batch and attempt routing context");

        if(context.Intents.Count == 0)
            return PayoutAttemptSendResult.FailedPreAccept(InvalidRecipientErrorCode,
                "Bitcoin RPC sender requires at least one payout recipient");

        foreach(var intent in context.Intents)
        {
            if(intent == null)
                return PayoutAttemptSendResult.FailedPreAccept(InvalidContextErrorCode,
                    "Bitcoin RPC sender requires non-null payout intent context");

            if(!string.Equals(intent.PoolId, context.Attempt.PoolId, StringComparison.Ordinal) ||
               !string.Equals(intent.Coin, context.Attempt.Coin, StringComparison.Ordinal) ||
               intent.AttemptId != context.Attempt.Id ||
               !IsCompatibleIntentState(intent.IntentState) ||
               !string.Equals(intent.AttemptIntentState, PayoutAttemptIntentStates.Active, StringComparison.Ordinal))
                return PayoutAttemptSendResult.FailedPreAccept(InvalidContextErrorCode,
                    "Bitcoin RPC sender requires active executable intents matching the attempt routing context");

            if(string.IsNullOrWhiteSpace(intent.Address))
                return PayoutAttemptSendResult.FailedPreAccept(InvalidRecipientErrorCode,
                    "Bitcoin RPC sender requires non-empty payout recipient addresses");

            if(intent.Amount <= 0m)
                return PayoutAttemptSendResult.FailedPreAccept(InvalidAmountErrorCode,
                    "Bitcoin RPC sender requires positive payout recipient amounts");
        }

        return null;
    }

    private static bool MatchingNonEmptyValues(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.Ordinal);
    }

    private static bool IsCompatibleIntentState(string state)
    {
        return string.Equals(state, PayoutIntentStates.Reserved, StringComparison.Ordinal) ||
               string.Equals(state, PayoutIntentStates.Sending, StringComparison.Ordinal);
    }

    private static bool IsSendManyContext(PayoutSendExecutionContext context)
    {
        return string.Equals(context.Batch.SendShape, PayoutProfileConstants.SendShapes.BatchMultiRecipient,
                   StringComparison.Ordinal) &&
               string.Equals(context.Attempt.Method, PayoutProfileConstants.SendMethods.SendMany,
                   StringComparison.Ordinal);
    }

    private static bool IsSendToAddressContext(PayoutSendExecutionContext context)
    {
        return string.Equals(context.Batch.SendShape, PayoutProfileConstants.SendShapes.PerAddress,
                   StringComparison.Ordinal) &&
               string.Equals(context.Attempt.Method, PayoutProfileConstants.SendMethods.SendToAddress,
                   StringComparison.Ordinal);
    }

    private static BitcoinPayoutSendManyRequest CreateSendManyRequest(PayoutSendExecutionContext context)
    {
        var recipients = context.Intents
            .GroupBy(x => x.Address, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Amount), StringComparer.Ordinal);

        return new BitcoinPayoutSendManyRequest
        {
            PoolId = context.Attempt.PoolId,
            Coin = context.Attempt.Coin,
            BatchId = context.Attempt.BatchId,
            AttemptId = context.Attempt.Id,
            Method = context.Attempt.Method,
            Recipients = new ReadOnlyDictionary<string, decimal>(recipients)
        };
    }

    private static BitcoinPayoutSendToAddressRequest CreateSendToAddressRequest(PayoutSendExecutionContext context)
    {
        var intent = context.Intents.Single();
        return new BitcoinPayoutSendToAddressRequest
        {
            PoolId = context.Attempt.PoolId,
            Coin = context.Attempt.Coin,
            BatchId = context.Attempt.BatchId,
            AttemptId = context.Attempt.Id,
            Method = context.Attempt.Method,
            Address = intent.Address,
            Amount = intent.Amount
        };
    }

    private PayoutAttemptSendResult MapRpcResult(BitcoinPayoutRpcResult result)
    {
        if(result == null)
            return PayoutAttemptSendResult.Ambiguous(InvalidRpcResultErrorCode,
                "Bitcoin RPC sender returned no result");

        switch(result.Status)
        {
            case BitcoinPayoutRpcStatus.Accepted:
                if(evidenceValidator.IsUnsafeEvidenceValue(result.TxId))
                    return PayoutAttemptSendResult.Ambiguous(InvalidRpcResultErrorCode,
                        "Bitcoin RPC sender returned an unsafe or empty transaction id after possible submission");

                return PayoutAttemptSendResult.Accepted(new PayoutAttemptEvidence
                {
                    Kind = PayoutExternalConfirmationKinds.TxId,
                    Value = result.TxId
                });

            case BitcoinPayoutRpcStatus.FailedPreAccept:
                return PayoutAttemptSendResult.FailedPreAccept(
                    NormalizeErrorCode(result.ErrorCode, InvalidRpcResultErrorCode),
                    result.ErrorMessage);

            case BitcoinPayoutRpcStatus.AmbiguousRequiresReview:
                return PayoutAttemptSendResult.Ambiguous(
                    NormalizeErrorCode(result.ErrorCode, InvalidRpcResultErrorCode),
                    result.ErrorMessage);

            default:
                return PayoutAttemptSendResult.Ambiguous(InvalidRpcResultErrorCode,
                    "Bitcoin RPC sender returned an unknown result status");
        }
    }

    private static string NormalizeErrorCode(string errorCode, string fallback)
    {
        return string.IsNullOrWhiteSpace(errorCode) ? fallback : errorCode;
    }
}
