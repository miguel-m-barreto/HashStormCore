using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;

namespace HashStormCore.Payments;

#nullable enable annotations
public class PayoutExecutionEvidenceValidator
{
    public bool TryValidatePrimaryEvidenceForProfile(PayoutProfile profile, PayoutAttemptEvidence? evidence,
        out string errorMessage)
    {
        if(profile == null)
        {
            errorMessage = "Profile is required for evidence kind validation";
            return false;
        }

        if(evidence == null)
        {
            errorMessage = "Primary evidence is required";
            return false;
        }

        if(string.IsNullOrWhiteSpace(evidence.Kind))
        {
            errorMessage = "Primary evidence kind is empty";
            return false;
        }

        if(IsUnsafeEvidenceValue(evidence.Value))
        {
            errorMessage = $"Primary evidence value is unsafe or fake for kind '{evidence.Kind}'";
            return false;
        }

        var expectedKind = GetExpectedPrimaryEvidenceKind(profile);
        if(expectedKind == null)
        {
            errorMessage = $"Profile settlement evidence kind '{profile.SettlementEvidenceKind}' does not define a valid primary execution evidence kind";
            return false;
        }

        if(!string.Equals(evidence.Kind, expectedKind, StringComparison.Ordinal))
        {
            errorMessage = $"Profile requires primary evidence kind '{expectedKind}' but sender returned '{evidence.Kind}'";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool TryValidateFinalAcceptedEvidenceForProfile(PayoutProfile profile, PayoutAttemptEvidence? evidence,
        out string errorMessage)
    {
        if(profile == null)
        {
            errorMessage = "Profile is required for final accepted evidence validation";
            return false;
        }

        if(evidence == null)
        {
            errorMessage = "Final accepted evidence is required";
            return false;
        }

        if(string.IsNullOrWhiteSpace(evidence.Kind))
        {
            errorMessage = "Final accepted evidence kind is empty";
            return false;
        }

        if(IsUnsafeEvidenceValue(evidence.Value))
        {
            errorMessage = $"Final accepted evidence value is unsafe or fake for kind '{evidence.Kind}'";
            return false;
        }

        var expectedKind = GetExpectedFinalAcceptedEvidenceKind(profile);
        if(expectedKind == null)
        {
            errorMessage = $"Profile settlement evidence kind '{profile.SettlementEvidenceKind}' does not define valid final accepted evidence";
            return false;
        }

        if(!string.Equals(evidence.Kind, expectedKind, StringComparison.Ordinal))
        {
            errorMessage = $"Profile requires final accepted evidence kind '{expectedKind}' but review provided '{evidence.Kind}'";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool TryValidateAdditionalEvidence(PayoutProfile profile, PayoutAttemptEvidence? primary,
        IReadOnlyCollection<PayoutAttemptEvidence>? additional, out string errorMessage)
    {
        if(additional == null || additional.Count == 0)
        {
            errorMessage = string.Empty;
            return true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach(var ev in additional)
        {
            if(ev == null)
            {
                errorMessage = "Additional evidence entry is null";
                return false;
            }

            if(string.IsNullOrWhiteSpace(ev.Kind))
            {
                errorMessage = "Additional evidence entry has empty kind";
                return false;
            }

            if(IsUnsafeEvidenceValue(ev.Value))
            {
                errorMessage = $"Additional evidence has unsafe or fake value for kind '{ev.Kind}'";
                return false;
            }

            if(!IsKnownEvidenceKind(ev.Kind))
            {
                errorMessage = $"Additional evidence has unsupported kind '{ev.Kind}'";
                return false;
            }

            if(string.Equals(ev.Kind, PayoutExternalConfirmationKinds.WalletAck, StringComparison.Ordinal))
            {
                errorMessage = "wallet_ack is not accepted as evidence for any current payout profile";
                return false;
            }

            if(!seen.Add($"{ev.Kind}\x00{ev.Value}"))
            {
                errorMessage = $"Additional evidence contains duplicate kind/value for kind '{ev.Kind}'";
                return false;
            }

            if(primary != null &&
               string.Equals(ev.Kind, primary.Kind, StringComparison.Ordinal) &&
               string.Equals(ev.Value, primary.Value, StringComparison.Ordinal))
            {
                errorMessage = $"Additional evidence duplicates the primary evidence for kind '{ev.Kind}'";
                return false;
            }

            // async_operation: block txid/raw_hash in additional — settlement evidence must come from reconciliation only
            if(IsAsyncOperationProfile(profile) &&
               (string.Equals(ev.Kind, PayoutExternalConfirmationKinds.TxId, StringComparison.Ordinal) ||
                string.Equals(ev.Kind, PayoutExternalConfirmationKinds.RawHash, StringComparison.Ordinal)))
            {
                errorMessage = $"Async-operation profiles must not include '{ev.Kind}' in additional evidence; txid must come from operation-id reconciliation only";
                return false;
            }

            // txid/raw_hash profiles: block operation_id in additional
            if(IsDirectSettlementProfile(profile) &&
               string.Equals(ev.Kind, PayoutExternalConfirmationKinds.OperationId, StringComparison.Ordinal))
            {
                errorMessage = "txid and raw_hash profiles must not include operation_id in additional evidence";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool IsUnsafeEvidenceValue(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))
            return true;

        if(value.StartsWith("send:", StringComparison.OrdinalIgnoreCase))
            return true;

        if(value.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public bool IsSettlementEligibleByProfile(PayoutProfile profile)
    {
        if(profile == null || !profile.ReservationReady)
            return false;

        return IsDirectSettlementProfile(profile) ||
               string.Equals(profile.SettlementEvidenceKind,
                   PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId, StringComparison.Ordinal);
    }

    public bool IsOperationIdReconciliationEligibleByProfile(PayoutProfile profile)
    {
        if(profile == null || !profile.ReservationReady)
            return false;

        return string.Equals(profile.SendShape, PayoutProfileConstants.SendShapes.AsyncOperation,
                   StringComparison.Ordinal) &&
               string.Equals(profile.SettlementEvidenceKind,
                   PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId, StringComparison.Ordinal) &&
               profile.RequiresOperationIdProvider &&
               profile.SupportsShieldedOperationTracking;
    }

    public bool IsFinalSettlementEvidenceKind(string kind)
    {
        return string.Equals(kind, PayoutExternalConfirmationKinds.TxId, StringComparison.Ordinal) ||
               string.Equals(kind, PayoutExternalConfirmationKinds.RawHash, StringComparison.Ordinal);
    }

    private static string? GetExpectedPrimaryEvidenceKind(PayoutProfile profile)
    {
        var kind = profile.SettlementEvidenceKind;

        if(string.Equals(kind, PayoutProfileConstants.SettlementEvidenceKinds.TxId, StringComparison.Ordinal))
            return PayoutExternalConfirmationKinds.TxId;

        if(string.Equals(kind, PayoutProfileConstants.SettlementEvidenceKinds.RawHash, StringComparison.Ordinal))
            return PayoutExternalConfirmationKinds.RawHash;

        if(string.Equals(kind, PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId,
               StringComparison.Ordinal) &&
           string.Equals(profile.SendShape, PayoutProfileConstants.SendShapes.AsyncOperation,
               StringComparison.Ordinal))
            return PayoutExternalConfirmationKinds.OperationId;

        return null;
    }

    private static string? GetExpectedFinalAcceptedEvidenceKind(PayoutProfile profile)
    {
        var kind = profile.SettlementEvidenceKind;

        if(string.Equals(kind, PayoutProfileConstants.SettlementEvidenceKinds.TxId, StringComparison.Ordinal))
            return PayoutExternalConfirmationKinds.TxId;

        if(string.Equals(kind, PayoutProfileConstants.SettlementEvidenceKinds.RawHash, StringComparison.Ordinal))
            return PayoutExternalConfirmationKinds.RawHash;

        if(string.Equals(kind, PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId,
               StringComparison.Ordinal) &&
           string.Equals(profile.SendShape, PayoutProfileConstants.SendShapes.AsyncOperation,
               StringComparison.Ordinal))
            return PayoutExternalConfirmationKinds.TxId;

        return null;
    }

    private static bool IsAsyncOperationProfile(PayoutProfile profile)
    {
        return string.Equals(profile.SendShape, PayoutProfileConstants.SendShapes.AsyncOperation,
                   StringComparison.Ordinal) &&
               string.Equals(profile.SettlementEvidenceKind,
                   PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId, StringComparison.Ordinal);
    }

    private static bool IsDirectSettlementProfile(PayoutProfile profile)
    {
        return string.Equals(profile.SettlementEvidenceKind, PayoutProfileConstants.SettlementEvidenceKinds.TxId,
                   StringComparison.Ordinal) ||
               string.Equals(profile.SettlementEvidenceKind, PayoutProfileConstants.SettlementEvidenceKinds.RawHash,
                   StringComparison.Ordinal);
    }

    private static bool IsKnownEvidenceKind(string kind)
    {
        return string.Equals(kind, PayoutExternalConfirmationKinds.TxId, StringComparison.Ordinal) ||
               string.Equals(kind, PayoutExternalConfirmationKinds.RawHash, StringComparison.Ordinal) ||
               string.Equals(kind, PayoutExternalConfirmationKinds.OperationId, StringComparison.Ordinal) ||
               string.Equals(kind, PayoutExternalConfirmationKinds.WalletAck, StringComparison.Ordinal);
    }
}
#nullable restore
