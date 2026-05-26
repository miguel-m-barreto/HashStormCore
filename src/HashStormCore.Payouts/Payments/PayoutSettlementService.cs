using System.Data;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class PayoutSettlementService
{
    public PayoutSettlementService(IPayoutSettlementRepository payoutSettlementRepo,
        IPayoutProfileResolver profileResolver)
    {
        this.payoutSettlementRepo = payoutSettlementRepo ??
            throw new ArgumentNullException(nameof(payoutSettlementRepo));
        this.profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        evidenceValidator = new PayoutExecutionEvidenceValidator();
    }

    private readonly IPayoutSettlementRepository payoutSettlementRepo;
    private readonly IPayoutProfileResolver profileResolver;
    private readonly PayoutExecutionEvidenceValidator evidenceValidator;

    public PayoutSettlementEligibilityResult ValidateSettlementCandidate(PayoutSettlementAttemptCandidate candidate)
    {
        if(candidate == null)
            throw new ArgumentNullException(nameof(candidate));

        var resolution = profileResolver.Resolve(candidate.Coin);

        if(!resolution.HasProfile || !resolution.Profile.ReservationReady)
        {
            var reason = resolution.HasProfile
                ? (resolution.Profile.NotReadyReason ?? resolution.Reason)
                : resolution.Reason;
            return PayoutSettlementEligibilityResult.ProfileNotReady(
                string.IsNullOrWhiteSpace(reason) ? "Profile not ready for settlement" : reason);
        }

        var profile = resolution.Profile;

        if(!evidenceValidator.IsSettlementEligibleByProfile(profile))
            return PayoutSettlementEligibilityResult.EvidenceKindNotSupported(
                $"Profile settlement evidence kind '{profile.SettlementEvidenceKind}' is not eligible for settlement; " +
                "operation_id-only and wallet_ack profiles cannot settle");

        var expectedEvidenceKind = GetExpectedFinalSettlementEvidenceKind(profile);
        if(string.IsNullOrWhiteSpace(expectedEvidenceKind))
            return PayoutSettlementEligibilityResult.EvidenceKindNotSupported(
                $"Profile settlement evidence kind '{profile.SettlementEvidenceKind}' does not define final settlement evidence");

        if(!IsKnownFinalSettlementEvidenceKind(candidate.EvidenceKind) ||
           !string.Equals(candidate.EvidenceKind, expectedEvidenceKind, StringComparison.Ordinal))
            return PayoutSettlementEligibilityResult.EvidenceKindNotSupported(
                $"Profile requires final settlement evidence kind '{expectedEvidenceKind}' but candidate has '{candidate.EvidenceKind}'");

        if(evidenceValidator.IsUnsafeEvidenceValue(candidate.TransactionConfirmationData))
            return PayoutSettlementEligibilityResult.UnsafeEvidenceValue(
                "Transaction confirmation data is missing, empty, or unsafe");

        return PayoutSettlementEligibilityResult.Eligible();
    }

    public async Task<PayoutSettlementResult> SettleEligibleCandidateAsync(IDbConnection con, IDbTransaction tx,
        PayoutSettlementAttemptCandidate candidate, DateTime settledAt, CancellationToken ct)
    {
        if(candidate == null)
            throw new ArgumentNullException(nameof(candidate));

        var eligibility = ValidateSettlementCandidate(candidate);
        if(!eligibility.IsEligible)
            return PayoutSettlementResult.ProfileValidationFailed(candidate.BatchId, candidate.AttemptId);

        return await payoutSettlementRepo.SettleAcceptedAttemptAsync(con, tx, new PayoutSettlementRequest
        {
            PoolId = candidate.PoolId,
            BatchId = candidate.BatchId,
            AttemptId = candidate.AttemptId,
            ExpectedEvidenceKind = candidate.EvidenceKind,
            SettledAt = settledAt
        }, ct);
    }

    private static string GetExpectedFinalSettlementEvidenceKind(PayoutProfile profile)
    {
        if(string.Equals(profile.SettlementEvidenceKind, PayoutProfileConstants.SettlementEvidenceKinds.TxId,
               StringComparison.Ordinal))
            return PayoutExternalConfirmationKinds.TxId;

        if(string.Equals(profile.SettlementEvidenceKind, PayoutProfileConstants.SettlementEvidenceKinds.RawHash,
               StringComparison.Ordinal))
            return PayoutExternalConfirmationKinds.RawHash;

        if(string.Equals(profile.SettlementEvidenceKind,
               PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId, StringComparison.Ordinal) &&
           string.Equals(profile.SendShape, PayoutProfileConstants.SendShapes.AsyncOperation, StringComparison.Ordinal))
            return PayoutExternalConfirmationKinds.TxId;

        return null;
    }

    private static bool IsKnownFinalSettlementEvidenceKind(string evidenceKind)
    {
        return string.Equals(evidenceKind, PayoutExternalConfirmationKinds.TxId, StringComparison.Ordinal) ||
               string.Equals(evidenceKind, PayoutExternalConfirmationKinds.RawHash, StringComparison.Ordinal);
    }
}
