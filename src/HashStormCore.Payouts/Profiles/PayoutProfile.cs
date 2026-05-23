namespace HashStormCore.Payouts.Profiles;

public record PayoutProfile
{
    public string CoinKey { get; init; } = string.Empty;
    public string CoinSymbol { get; init; } = string.Empty;
    // Original metadata family from coins.json. AdapterId is the effective payout execution profile.
    public string CoinFamily { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
    public string SendShape { get; init; } = string.Empty;
    public string SendMethod { get; init; } = string.Empty;
    public string SettlementEvidenceKind { get; init; } = string.Empty;
    public bool RequiresOperationIdProvider { get; init; }
    public bool SupportsTransparentTxId { get; init; }
    public bool SupportsShieldedOperationTracking { get; init; }
    public bool AllowsBatchMultiRecipient { get; init; }
    public bool AllowsPerAddress { get; init; }
    public int? MaxRecipientsPerAttempt { get; init; }
    public bool MayReturnMultipleTransactionHashes { get; init; }
    public bool RequiresPerIntentEvidenceMapping { get; init; }
    public bool RequiresWalletDaemon { get; init; }
    public bool RequiresExternalWalletWrapper { get; init; }
    public bool RequiresPrivateKeyMaterial { get; init; }
    public bool PlaceholderEvidenceUnsafe { get; init; }
    public bool ReservationReady { get; init; }
    public string NotReadyReason { get; init; } = string.Empty;
}
