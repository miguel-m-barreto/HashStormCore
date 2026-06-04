using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using Xunit;

namespace HashStormCore.Tests.Payouts;

#nullable enable annotations
public class PayoutExecutionEvidenceValidatorTests
{
    private readonly PayoutExecutionEvidenceValidator validator = new();

    // ── TxId profile ─────────────────────────────────────────────────────────

    [Fact]
    public void TxIdProfile_AcceptsTxIdPrimaryEvidence()
    {
        var profile = TxIdProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.TxId, "abc123txid"), out _);
        Assert.True(result);
    }

    [Fact]
    public void TxIdProfile_RejectsRawHashPrimaryEvidence()
    {
        var profile = TxIdProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.RawHash, "rawhash-value"), out var error);
        Assert.False(result);
        Assert.Contains(PayoutExternalConfirmationKinds.TxId, error);
    }

    [Fact]
    public void TxIdProfile_RejectsOperationIdPrimaryEvidence()
    {
        var profile = TxIdProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.OperationId, "opid-value"), out _);
        Assert.False(result);
    }

    [Fact]
    public void TxIdProfile_RejectsWalletAckPrimaryEvidence()
    {
        var profile = TxIdProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.WalletAck, "ack-value"), out _);
        Assert.False(result);
    }

    // ── RawHash profile ───────────────────────────────────────────────────────

    [Fact]
    public void RawHashProfile_AcceptsRawHashPrimaryEvidence()
    {
        var profile = RawHashProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.RawHash, "abc123rawhash"), out _);
        Assert.True(result);
    }

    [Fact]
    public void RawHashProfile_RejectsTxIdPrimaryEvidence()
    {
        var profile = RawHashProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.TxId, "txid-value"), out var error);
        Assert.False(result);
        Assert.Contains(PayoutExternalConfirmationKinds.RawHash, error);
    }

    [Fact]
    public void RawHashProfile_RejectsOperationIdPrimaryEvidence()
    {
        var profile = RawHashProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.OperationId, "opid-value"), out _);
        Assert.False(result);
    }

    // ── AsyncOperation (operationid_then_txid) profile ───────────────────────

    [Fact]
    public void AsyncOperationProfile_AcceptsOperationIdPrimaryEvidence()
    {
        var profile = AsyncOperationProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.OperationId, "opid-submission"), out _);
        Assert.True(result);
    }

    [Fact]
    public void AsyncOperationProfile_RejectsPrimaryTxId()
    {
        var profile = AsyncOperationProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.TxId, "txid-value"), out var error);
        Assert.False(result);
        Assert.Contains(PayoutExternalConfirmationKinds.OperationId, error);
    }

    [Fact]
    public void AsyncOperationProfile_RejectsPrimaryRawHash()
    {
        var profile = AsyncOperationProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.RawHash, "rawhash-value"), out _);
        Assert.False(result);
    }

    [Fact]
    public void AsyncOperationProfile_RejectsPrimaryWalletAck()
    {
        var profile = AsyncOperationProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.WalletAck, "ack-value"), out _);
        Assert.False(result);
    }

    // ── Final accepted review evidence rules ─────────────────────────────────

    [Fact]
    public void TxIdProfile_AcceptsOnlyTxIdFinalAcceptedEvidence()
    {
        var profile = TxIdProfile();

        Assert.True(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.TxId, "txid-reviewed"), out _));
        Assert.False(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.RawHash, "rawhash-reviewed"), out _));
        Assert.False(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.OperationId, "operationid-reviewed"), out _));
        Assert.False(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.WalletAck, "wallet-ack-reviewed"), out _));
    }

    [Fact]
    public void RawHashProfile_AcceptsOnlyRawHashFinalAcceptedEvidence()
    {
        var profile = RawHashProfile();

        Assert.True(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.RawHash, "rawhash-reviewed"), out _));
        Assert.False(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.TxId, "txid-reviewed"), out _));
    }

    [Fact]
    public void AsyncOperationProfile_AcceptsFinalTxIdButRejectsOperationIdAndWalletAck()
    {
        var profile = AsyncOperationProfile();

        Assert.True(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.TxId, "txid-reviewed"), out _));
        Assert.False(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.OperationId, "operationid-not-final"), out _));
        Assert.False(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.WalletAck, "wallet-ack-reviewed"), out _));
    }

    [Fact]
    public void FinalAcceptedEvidence_RejectsUnsafeValues()
    {
        var profile = TxIdProfile();

        Assert.False(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.TxId, "send:fake-reviewed"), out _));
        Assert.False(validator.TryValidateFinalAcceptedEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.TxId, "placeholder-reviewed"), out _));
    }

    [Theory]
    [InlineData(PayoutExternalConfirmationKinds.TxId, true)]
    [InlineData(PayoutExternalConfirmationKinds.RawHash, true)]
    [InlineData(PayoutExternalConfirmationKinds.OperationId, false)]
    [InlineData(PayoutExternalConfirmationKinds.WalletAck, false)]
    public void IsFinalSettlementEvidenceKind_ReturnsOnlyTxIdAndRawHash(string kind, bool expected)
    {
        Assert.Equal(expected, validator.IsFinalSettlementEvidenceKind(kind));
    }

    // ── Unsafe evidence values ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("send:something")]
    [InlineData("SEND:UPPER")]
    [InlineData("has-placeholder-in-it")]
    [InlineData("PLACEHOLDER")]
    [InlineData("some_placeholder_value")]
    public void UnsafeValuesAreRejected(string? value)
    {
        Assert.True(validator.IsUnsafeEvidenceValue(value));
    }

    [Theory]
    [InlineData("abc123realvalue")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("txhash-ok")]
    public void SafeValuesAreNotFlagged(string value)
    {
        Assert.False(validator.IsUnsafeEvidenceValue(value));
    }

    [Fact]
    public void TxIdProfile_RejectsUnsafeValueSendPrefix()
    {
        var profile = TxIdProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.TxId, "send:fake-txid"), out var error);
        Assert.False(result);
        Assert.Contains("unsafe", error);
    }

    [Fact]
    public void TxIdProfile_RejectsPlaceholderValue()
    {
        var profile = TxIdProfile();
        var result = validator.TryValidatePrimaryEvidenceForProfile(profile,
            Evidence(PayoutExternalConfirmationKinds.TxId, "fake-placeholder-txid"), out _);
        Assert.False(result);
    }

    // ── Additional evidence rules ─────────────────────────────────────────────

    [Fact]
    public void AdditionalEvidence_NullOrEmptyListPassesValidation()
    {
        var profile = TxIdProfile();
        var primary = Evidence(PayoutExternalConfirmationKinds.TxId, "txid-primary");
        Assert.True(validator.TryValidateAdditionalEvidence(profile, primary, null, out _));
        Assert.True(validator.TryValidateAdditionalEvidence(profile, primary, Array.Empty<PayoutAttemptEvidence>(), out _));
    }

    [Fact]
    public void AdditionalEvidence_DuplicateKindValuePairIsRejected()
    {
        var profile = TxIdProfile();
        var primary = Evidence(PayoutExternalConfirmationKinds.TxId, "txid-primary");
        var additional = new[]
        {
            Evidence(PayoutExternalConfirmationKinds.RawHash, "rawhash-value"),
            Evidence(PayoutExternalConfirmationKinds.RawHash, "rawhash-value")
        };
        var result = validator.TryValidateAdditionalEvidence(profile, primary, additional, out var error);
        Assert.False(result);
        Assert.Contains("duplicate", error);
    }

    [Fact]
    public void AdditionalEvidence_CannotDuplicatePrimaryEvidence()
    {
        var profile = TxIdProfile();
        var primary = Evidence(PayoutExternalConfirmationKinds.TxId, "txid-primary");
        var additional = new[] { Evidence(PayoutExternalConfirmationKinds.TxId, "txid-primary") };
        var result = validator.TryValidateAdditionalEvidence(profile, primary, additional, out var error);
        Assert.False(result);
        Assert.Contains("duplicates", error);
    }

    [Fact]
    public void AdditionalEvidence_WalletAckIsRejectedForAllProfiles()
    {
        foreach(var profile in new[] { TxIdProfile(), RawHashProfile(), AsyncOperationProfile() })
        {
            var primary = Evidence(PayoutExternalConfirmationKinds.OperationId, "opid");
            var additional = new[] { Evidence(PayoutExternalConfirmationKinds.WalletAck, "ack-value") };
            var result = validator.TryValidateAdditionalEvidence(profile, primary, additional, out var error);
            Assert.False(result);
            Assert.Contains("wallet_ack", error);
        }
    }

    [Fact]
    public void AsyncOperationProfile_RejectsTxIdInAdditionalEvidence()
    {
        var profile = AsyncOperationProfile();
        var primary = Evidence(PayoutExternalConfirmationKinds.OperationId, "opid-value");
        var additional = new[] { Evidence(PayoutExternalConfirmationKinds.TxId, "txid-bypass-attempt") };
        var result = validator.TryValidateAdditionalEvidence(profile, primary, additional, out var error);
        Assert.False(result);
        Assert.Contains("txid", error);
    }

    [Fact]
    public void AsyncOperationProfile_RejectsRawHashInAdditionalEvidence()
    {
        var profile = AsyncOperationProfile();
        var primary = Evidence(PayoutExternalConfirmationKinds.OperationId, "opid-value");
        var additional = new[] { Evidence(PayoutExternalConfirmationKinds.RawHash, "rawhash-bypass") };
        var result = validator.TryValidateAdditionalEvidence(profile, primary, additional, out _);
        Assert.False(result);
    }

    [Fact]
    public void TxIdProfile_RejectsOperationIdInAdditionalEvidence()
    {
        var profile = TxIdProfile();
        var primary = Evidence(PayoutExternalConfirmationKinds.TxId, "txid-primary");
        var additional = new[] { Evidence(PayoutExternalConfirmationKinds.OperationId, "spurious-opid") };
        var result = validator.TryValidateAdditionalEvidence(profile, primary, additional, out var error);
        Assert.False(result);
        Assert.Contains("operation_id", error);
    }

    [Fact]
    public void RawHashProfile_RejectsOperationIdInAdditionalEvidence()
    {
        var profile = RawHashProfile();
        var primary = Evidence(PayoutExternalConfirmationKinds.RawHash, "rawhash-primary");
        var additional = new[] { Evidence(PayoutExternalConfirmationKinds.OperationId, "spurious-opid") };
        var result = validator.TryValidateAdditionalEvidence(profile, primary, additional, out _);
        Assert.False(result);
    }

    // ── Settlement eligibility ────────────────────────────────────────────────

    [Fact]
    public void IsSettlementEligibleByProfile_TxIdProfileIsEligible()
        => Assert.True(validator.IsSettlementEligibleByProfile(TxIdProfile()));

    [Fact]
    public void IsSettlementEligibleByProfile_RawHashProfileIsEligible()
        => Assert.True(validator.IsSettlementEligibleByProfile(RawHashProfile()));

    [Fact]
    public void IsSettlementEligibleByProfile_AsyncOperationProfileIsEligible()
        => Assert.True(validator.IsSettlementEligibleByProfile(AsyncOperationProfile()));

    [Fact]
    public void IsSettlementEligibleByProfile_OperationIdOnlyProfileIsNotEligible()
    {
        var profile = new PayoutProfile
        {
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.OperationId,
            ReservationReady = true
        };
        Assert.False(validator.IsSettlementEligibleByProfile(profile));
    }

    [Fact]
    public void IsSettlementEligibleByProfile_WalletAckProfileIsNotEligible()
    {
        var profile = new PayoutProfile
        {
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.Unsupported,
            ReservationReady = true
        };
        Assert.False(validator.IsSettlementEligibleByProfile(profile));
    }

    [Fact]
    public void IsSettlementEligibleByProfile_ProfileNotReadyIsNotEligible()
    {
        var profile = TxIdProfile() with { ReservationReady = false };
        Assert.False(validator.IsSettlementEligibleByProfile(profile));
    }

    // ── Operation-id reconciliation eligibility ───────────────────────────────

    [Fact]
    public void IsOperationIdReconciliationEligible_AsyncOperationProfileWithAllRequiredFlagsIsEligible()
        => Assert.True(validator.IsOperationIdReconciliationEligibleByProfile(AsyncOperationProfile()));

    [Fact]
    public void IsOperationIdReconciliationEligible_TxIdProfileIsNotEligible()
        => Assert.False(validator.IsOperationIdReconciliationEligibleByProfile(TxIdProfile()));

    [Fact]
    public void IsOperationIdReconciliationEligible_RawHashProfileIsNotEligible()
        => Assert.False(validator.IsOperationIdReconciliationEligibleByProfile(RawHashProfile()));

    [Fact]
    public void IsOperationIdReconciliationEligible_AsyncProfileWithoutOperationIdProviderIsNotEligible()
    {
        var profile = AsyncOperationProfile() with { RequiresOperationIdProvider = false };
        Assert.False(validator.IsOperationIdReconciliationEligibleByProfile(profile));
    }

    [Fact]
    public void IsOperationIdReconciliationEligible_AsyncProfileWithoutShieldedTrackingIsNotEligible()
    {
        var profile = AsyncOperationProfile() with { SupportsShieldedOperationTracking = false };
        Assert.False(validator.IsOperationIdReconciliationEligibleByProfile(profile));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PayoutProfile TxIdProfile()
    {
        return new PayoutProfile
        {
            CoinKey = "test-txid",
            CoinFamily = "bitcoin",
            AdapterId = "bitcoin-rpc",
            SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
            SendMethod = "sendmany",
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
            ReservationReady = true
        };
    }

    private static PayoutProfile RawHashProfile()
    {
        return new PayoutProfile
        {
            CoinKey = "test-rawhash",
            CoinFamily = "warthog",
            AdapterId = "warthog-rest-signed",
            SendShape = PayoutProfileConstants.SendShapes.PerAddress,
            SendMethod = "transaction/add",
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.RawHash,
            ReservationReady = true
        };
    }

    private static PayoutProfile AsyncOperationProfile()
    {
        return new PayoutProfile
        {
            CoinKey = "test-async",
            CoinFamily = "equihash",
            AdapterId = "equihash-z-async",
            SendShape = PayoutProfileConstants.SendShapes.AsyncOperation,
            SendMethod = "z_sendmany",
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId,
            RequiresOperationIdProvider = true,
            SupportsShieldedOperationTracking = true,
            MaxRecipientsPerAttempt = 50,
            ReservationReady = true
        };
    }

    private static PayoutAttemptEvidence Evidence(string kind, string value)
    {
        return new PayoutAttemptEvidence { Kind = kind, Value = value };
    }
}
#nullable restore
