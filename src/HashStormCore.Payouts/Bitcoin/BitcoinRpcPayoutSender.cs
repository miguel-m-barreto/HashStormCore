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
            return MapRpcResult(await rpcClient.SendManyAsync(CreateSendManyRequest(context.Intents), ct));

        if(IsSendToAddressContext(context))
        {
            if(context.Intents.Count != 1)
                return PayoutAttemptSendResult.FailedPreAccept(InvalidRecipientErrorCode,
                    "Bitcoin sendtoaddress requires exactly one payout recipient");

            return MapRpcResult(await rpcClient.SendToAddressAsync(CreateSendToAddressRequest(context.Intents), ct));
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

        if(context.Intents.Count == 0)
            return PayoutAttemptSendResult.FailedPreAccept(InvalidRecipientErrorCode,
                "Bitcoin RPC sender requires at least one payout recipient");

        foreach(var intent in context.Intents)
        {
            if(intent == null || string.IsNullOrWhiteSpace(intent.Address))
                return PayoutAttemptSendResult.FailedPreAccept(InvalidRecipientErrorCode,
                    "Bitcoin RPC sender requires non-empty payout recipient addresses");

            if(intent.Amount <= 0m)
                return PayoutAttemptSendResult.FailedPreAccept(InvalidAmountErrorCode,
                    "Bitcoin RPC sender requires positive payout recipient amounts");
        }

        return null;
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

    private static BitcoinPayoutSendManyRequest CreateSendManyRequest(
        IReadOnlyCollection<PayoutSendExecutionIntent> intents)
    {
        var recipients = intents
            .GroupBy(x => x.Address, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Amount), StringComparer.Ordinal);

        return new BitcoinPayoutSendManyRequest
        {
            Recipients = new ReadOnlyDictionary<string, decimal>(recipients)
        };
    }

    private static BitcoinPayoutSendToAddressRequest CreateSendToAddressRequest(
        IReadOnlyCollection<PayoutSendExecutionIntent> intents)
    {
        var intent = intents.Single();
        return new BitcoinPayoutSendToAddressRequest
        {
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
