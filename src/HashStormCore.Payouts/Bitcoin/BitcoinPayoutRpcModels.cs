using System.Collections.ObjectModel;

namespace HashStormCore.Payouts.Bitcoin;

public record BitcoinPayoutSendManyRequest
{
    public string PoolId { get; init; } = string.Empty;
    public string Coin { get; init; } = string.Empty;
    public long BatchId { get; init; }
    public long AttemptId { get; init; }
    public string Method { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, decimal> Recipients { get; init; } =
        new ReadOnlyDictionary<string, decimal>(new Dictionary<string, decimal>());
}

public record BitcoinPayoutSendToAddressRequest
{
    public string PoolId { get; init; } = string.Empty;
    public string Coin { get; init; } = string.Empty;
    public long BatchId { get; init; }
    public long AttemptId { get; init; }
    public string Method { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public decimal Amount { get; init; }
}

public enum BitcoinPayoutRpcStatus
{
    Accepted,
    FailedPreAccept,
    AmbiguousRequiresReview
}

public record BitcoinPayoutRpcResult
{
    public BitcoinPayoutRpcStatus Status { get; init; }
    public string TxId { get; init; }
    public string ErrorCode { get; init; }
    public string ErrorMessage { get; init; }

    public static BitcoinPayoutRpcResult Accepted(string txId)
    {
        return new BitcoinPayoutRpcResult
        {
            Status = BitcoinPayoutRpcStatus.Accepted,
            TxId = txId
        };
    }

    public static BitcoinPayoutRpcResult FailedPreAccept(string errorCode, string errorMessage)
    {
        return new BitcoinPayoutRpcResult
        {
            Status = BitcoinPayoutRpcStatus.FailedPreAccept,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    public static BitcoinPayoutRpcResult AmbiguousRequiresReview(string errorCode, string errorMessage)
    {
        return new BitcoinPayoutRpcResult
        {
            Status = BitcoinPayoutRpcStatus.AmbiguousRequiresReview,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }
}
