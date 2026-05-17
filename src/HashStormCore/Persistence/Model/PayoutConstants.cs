namespace HashStormCore.Persistence.Model;

public static class PayoutBatchStates
{
    public const string Reserved = "reserved";
    public const string Sending = "sending";
    public const string Submitted = "submitted";
    public const string Settled = "settled";
    public const string Failed = "failed";
    public const string AmbiguousRequiresReview = "ambiguous_requires_review";
    public const string Cancelled = "cancelled";
}

public static class PayoutIntentStates
{
    public const string Reserved = "reserved";
    public const string Sending = "sending";
    public const string Submitted = "submitted";
    public const string Settled = "settled";
    public const string Failed = "failed";
    public const string AmbiguousRequiresReview = "ambiguous_requires_review";
    public const string Cancelled = "cancelled";
}

public static class PayoutSendAttemptStates
{
    public const string Prepared = "prepared";
    public const string Sending = "sending";
    public const string Accepted = "accepted";
    public const string FailedPreAccept = "failed_pre_accept";
    public const string FailedNoAccept = "failed_no_accept";
    public const string AmbiguousRequiresReview = "ambiguous_requires_review";
}

public static class PayoutAttemptIntentStates
{
    public const string Active = "active";
    public const string Accepted = "accepted";
    public const string FailedPreAccept = "failed_pre_accept";
    public const string FailedNoAccept = "failed_no_accept";
    public const string AmbiguousRequiresReview = "ambiguous_requires_review";
    public const string Superseded = "superseded";
}

public static class PayoutExternalConfirmationKinds
{
    public const string TxId = "txid";
    public const string OperationId = "operationid";
    public const string WalletAck = "wallet_ack";
    public const string RawHash = "raw_hash";
}

public static class PayoutAdminActions
{
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string RunNow = "run_now";
    public const string CancelReserved = "cancel_reserved";
    public const string MarkFailedPreAccept = "mark_failed_pre_accept";
    public const string MarkFailedNoAccept = "mark_failed_no_accept";
    public const string AttachTxId = "attach_txid";
    public const string AttachOperationId = "attach_operationid";
    public const string SettleAmbiguous = "settle_ambiguous";
    public const string ReleaseAmbiguous = "release_ambiguous";
    public const string RetryAfterReview = "retry_after_review";
}

public static class PayoutSendShapes
{
    public const string BatchMultiRecipient = "batch_multi_recipient";
    public const string PerAddress = "per_address";
    public const string AddressGroup = "address_group";
    public const string AsyncOperation = "async_operation";
}
