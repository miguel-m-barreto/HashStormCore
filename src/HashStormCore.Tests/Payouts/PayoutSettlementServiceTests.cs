using System.Data;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class PayoutSettlementServiceTests
{
    // ── ValidateSettlementCandidate — eligible profiles ───────────────────────

    [Fact]
    public void ValidateSettlementCandidate_TxIdProfileIsEligible()
    {
        var service = NewService(TxIdProfile(), "txid-valid-confirmation");
        var result = service.ValidateSettlementCandidate(
            Candidate(PayoutExternalConfirmationKinds.TxId, "txid-valid-confirmation"));
        Assert.True(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.Eligible, result.Status);
    }

    [Fact]
    public void ValidateSettlementCandidate_RawHashProfileIsEligible()
    {
        var service = NewService(RawHashProfile(), "rawhash-valid-confirmation");
        var result = service.ValidateSettlementCandidate(
            Candidate(PayoutExternalConfirmationKinds.RawHash, "rawhash-valid-confirmation"));
        Assert.True(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.Eligible, result.Status);
    }

    [Fact]
    public void ValidateSettlementCandidate_AsyncOperationProfileIsEligible()
    {
        var service = NewService(AsyncOperationProfile(), "txid-from-reconciliation");
        var result = service.ValidateSettlementCandidate(
            Candidate(PayoutExternalConfirmationKinds.TxId, "txid-from-reconciliation"));
        Assert.True(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.Eligible, result.Status);
    }

    // ── ValidateSettlementCandidate — ineligible profiles ─────────────────────

    [Fact]
    public void ValidateSettlementCandidate_OperationIdOnlyProfileIsNotEligible()
    {
        var profile = new PayoutProfile
        {
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.OperationId,
            ReservationReady = true
        };
        var service = NewService(profile, "opid-confirmation");
        var result = service.ValidateSettlementCandidate(
            Candidate(PayoutExternalConfirmationKinds.OperationId, "opid-confirmation"));
        Assert.False(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.EvidenceKindNotSupported, result.Status);
    }

    [Fact]
    public void ValidateSettlementCandidate_UnsupportedEvidenceKindProfileIsNotEligible()
    {
        var profile = new PayoutProfile
        {
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.Unsupported,
            ReservationReady = true
        };
        var service = NewService(profile, "ack-confirmation");
        var result = service.ValidateSettlementCandidate(
            Candidate(PayoutExternalConfirmationKinds.WalletAck, "ack-confirmation"));
        Assert.False(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.EvidenceKindNotSupported, result.Status);
    }

    [Fact]
    public void ValidateSettlementCandidate_ProfileNotReadyIsNotEligible()
    {
        var profile = TxIdProfile() with { ReservationReady = false };
        var service = NewService(profile, "txid-valid");
        var result = service.ValidateSettlementCandidate(Candidate(PayoutExternalConfirmationKinds.TxId, "txid-valid"));
        Assert.False(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.ProfileNotReady, result.Status);
    }

    [Fact]
    public void ValidateSettlementCandidate_UnresolvableProfileIsNotEligible()
    {
        var service = NewServiceWithResolution(PayoutProfileResolution.Unsupported("coin not configured"), "txid-valid");
        var result = service.ValidateSettlementCandidate(Candidate(PayoutExternalConfirmationKinds.TxId, "txid-valid"));
        Assert.False(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.ProfileNotReady, result.Status);
    }

    [Fact]
    public void ValidateSettlementCandidate_UnsafeEvidenceValueIsNotEligible()
    {
        var service = NewService(TxIdProfile(), "send:fake-txid");
        var result = service.ValidateSettlementCandidate(Candidate(PayoutExternalConfirmationKinds.TxId, "send:fake-txid"));
        Assert.False(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.UnsafeEvidenceValue, result.Status);
    }

    [Fact]
    public void ValidateSettlementCandidate_EmptyEvidenceIsNotEligible()
    {
        var service = NewService(TxIdProfile(), "");
        var result = service.ValidateSettlementCandidate(Candidate(PayoutExternalConfirmationKinds.TxId, null));
        Assert.False(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.UnsafeEvidenceValue, result.Status);
    }

    [Fact]
    public void ValidateSettlementCandidate_PlaceholderEvidenceIsNotEligible()
    {
        var service = NewService(TxIdProfile(), "fake-placeholder-txid");
        var result = service.ValidateSettlementCandidate(
            Candidate(PayoutExternalConfirmationKinds.TxId, "fake-placeholder-txid"));
        Assert.False(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.UnsafeEvidenceValue, result.Status);
    }

    [Theory]
    [InlineData(PayoutExternalConfirmationKinds.RawHash)]
    [InlineData(PayoutExternalConfirmationKinds.OperationId)]
    [InlineData(PayoutExternalConfirmationKinds.WalletAck)]
    [InlineData("")]
    public void ValidateSettlementCandidate_TxIdProfileRejectsWrongEvidenceKind(string evidenceKind)
    {
        var service = NewService(TxIdProfile(), "settlement-value");
        var result = service.ValidateSettlementCandidate(Candidate(evidenceKind, "settlement-value"));
        Assert.False(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.EvidenceKindNotSupported, result.Status);
    }

    [Theory]
    [InlineData(PayoutExternalConfirmationKinds.TxId)]
    [InlineData(PayoutExternalConfirmationKinds.OperationId)]
    [InlineData(PayoutExternalConfirmationKinds.WalletAck)]
    public void ValidateSettlementCandidate_RawHashProfileRejectsWrongEvidenceKind(string evidenceKind)
    {
        var service = NewService(RawHashProfile(), "settlement-value");
        var result = service.ValidateSettlementCandidate(Candidate(evidenceKind, "settlement-value"));
        Assert.False(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.EvidenceKindNotSupported, result.Status);
    }

    [Theory]
    [InlineData(PayoutExternalConfirmationKinds.RawHash)]
    [InlineData(PayoutExternalConfirmationKinds.OperationId)]
    [InlineData(PayoutExternalConfirmationKinds.WalletAck)]
    public void ValidateSettlementCandidate_AsyncOperationProfileRejectsNonTxIdFinalEvidence(string evidenceKind)
    {
        var service = NewService(AsyncOperationProfile(), "settlement-value");
        var result = service.ValidateSettlementCandidate(Candidate(evidenceKind, "settlement-value"));
        Assert.False(result.IsEligible);
        Assert.Equal(PayoutSettlementEligibilityStatus.EvidenceKindNotSupported, result.Status);
    }

    [Fact]
    public void PayoutSettlementService_DoesNotExposeDirectSettleBypass()
    {
        Assert.DoesNotContain(typeof(PayoutSettlementService).GetMethods(),
            method => method.Name == "SettleAcceptedAttemptAsync" && method.DeclaringType == typeof(PayoutSettlementService));
    }

    // ── SettleEligibleCandidateAsync ──────────────────────────────────────────

    [Fact]
    public async Task SettleEligibleCandidateAsync_DelegatesToRepoForEligibleCandidate()
    {
        var repo = new CapturingSettlementRepository(PayoutSettlementResult.Settled(1, 2,
            new[] { 10L }, new[] { 20L }, new[] { 30L }, "txid-settled"));
        var resolver = new FixedProfileResolver(PayoutProfileResolution.Resolved(TxIdProfile()));
        var service = new PayoutSettlementService(repo, resolver);
        var candidate = Candidate(PayoutExternalConfirmationKinds.TxId, "txid-settled", batchId: 1, attemptId: 2,
            poolId: "pool-a");
        var settledAt = DateTime.UtcNow;

        var result = await service.SettleEligibleCandidateAsync(null, null, candidate, settledAt, CancellationToken.None);

        Assert.Equal(PayoutSettlementStatus.Settled, result.Status);
        Assert.NotNull(repo.CapturedRequest);
        Assert.Equal("pool-a", repo.CapturedRequest.PoolId);
        Assert.Equal(1L, repo.CapturedRequest.BatchId);
        Assert.Equal(2L, repo.CapturedRequest.AttemptId);
        Assert.Equal(PayoutExternalConfirmationKinds.TxId, repo.CapturedRequest.ExpectedEvidenceKind);
        Assert.Equal(settledAt, repo.CapturedRequest.SettledAt);
    }

    [Fact]
    public async Task SettleEligibleCandidateAsync_ReturnsProfileValidationFailedForIneligibleCandidate()
    {
        var repo = new CapturingSettlementRepository(PayoutSettlementResult.Settled(1, 2,
            Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>(), "should-not-reach"));
        var ineligibleProfile = new PayoutProfile
        {
            SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.OperationId,
            ReservationReady = true
        };
        var resolver = new FixedProfileResolver(PayoutProfileResolution.Resolved(ineligibleProfile));
        var service = new PayoutSettlementService(repo, resolver);
        var candidate = Candidate(PayoutExternalConfirmationKinds.OperationId, "opid-value", batchId: 5,
            attemptId: 7, poolId: "pool-b");

        var result = await service.SettleEligibleCandidateAsync(null, null, candidate, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(PayoutSettlementStatus.ProfileValidationFailed, result.Status);
        Assert.Equal(5L, result.BatchId);
        Assert.Equal(7L, result.AttemptId);
        Assert.Null(repo.CapturedRequest);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PayoutSettlementService NewService(PayoutProfile profile, string transactionConfirmationData)
    {
        var repo = new CapturingSettlementRepository(PayoutSettlementResult.Settled(1, 1,
            Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>(), transactionConfirmationData));
        var resolver = new FixedProfileResolver(PayoutProfileResolution.Resolved(profile));
        return new PayoutSettlementService(repo, resolver);
    }

    private static PayoutSettlementService NewServiceWithResolution(PayoutProfileResolution resolution,
        string transactionConfirmationData)
    {
        var repo = new CapturingSettlementRepository(PayoutSettlementResult.Settled(1, 1,
            Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>(), transactionConfirmationData));
        var resolver = new FixedProfileResolver(resolution);
        return new PayoutSettlementService(repo, resolver);
    }

    private static PayoutSettlementAttemptCandidate Candidate(string evidenceKind, string transactionConfirmationData,
        long batchId = 1, long attemptId = 1, string poolId = "test-pool")
    {
        return new PayoutSettlementAttemptCandidate
        {
            BatchId = batchId,
            AttemptId = attemptId,
            PoolId = poolId,
            Coin = "test-coin",
            Method = "test-method",
            EvidenceKind = evidenceKind,
            TransactionConfirmationData = transactionConfirmationData
        };
    }

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

    private class CapturingSettlementRepository : IPayoutSettlementRepository
    {
        public CapturingSettlementRepository(PayoutSettlementResult result)
        {
            this.result = result;
        }

        private readonly PayoutSettlementResult result;
        public PayoutSettlementRequest CapturedRequest { get; private set; }

        public Task<PayoutSettlementAttemptCandidate[]> GetAcceptedAttemptsForSettlementAsync(IDbConnection con,
            IDbTransaction tx, string poolId, int limit, CancellationToken ct)
            => Task.FromResult(Array.Empty<PayoutSettlementAttemptCandidate>());

        public Task<PayoutSettlementResult> SettleAcceptedAttemptAsync(IDbConnection con, IDbTransaction tx,
            PayoutSettlementRequest request, CancellationToken ct)
        {
            CapturedRequest = request;
            return Task.FromResult(result);
        }
    }

    private class FixedProfileResolver : IPayoutProfileResolver
    {
        public FixedProfileResolver(PayoutProfileResolution resolution)
        {
            this.resolution = resolution;
        }

        private readonly PayoutProfileResolution resolution;

        public PayoutProfileResolution Resolve(string coinKey) => resolution;
    }
}
