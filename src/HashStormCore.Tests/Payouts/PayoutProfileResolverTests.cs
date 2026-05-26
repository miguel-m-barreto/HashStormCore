using System;
using System.Collections.Generic;
using System.Linq;
using HashStormCore.Payouts.Alephium;
using HashStormCore.Payouts.Beam;
using HashStormCore.Payouts.Bitcoin;
using HashStormCore.Payouts.CoinMetadata;
using HashStormCore.Payouts.Conceal;
using HashStormCore.Payouts.Cryptonote;
using HashStormCore.Payouts.Equihash;
using HashStormCore.Payouts.Ergo;
using HashStormCore.Payouts.Ethereum;
using HashStormCore.Payouts.Handshake;
using HashStormCore.Payouts.Kaspa;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Payouts.Warthog;
using HashStormCore.Payouts.Xelis;
using HashStormCore.Payouts.Zano;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class PayoutProfileResolverTests
{
    [Fact]
    public void BitcoinFamilyCoinResolvesBitcoinSendMany()
    {
        var resolver = NewResolver(Coin("bitcoin", "bitcoin", "BTC"));

        var result = resolver.Resolve("bitcoin");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.NotNull(result.Profile);
        Assert.Equal(PayoutProfileConstants.AdapterIds.BitcoinRpc, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.BatchMultiRecipient, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendMany, result.Profile.SendMethod);
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.TxId, result.Profile.SettlementEvidenceKind);
        Assert.True(result.Profile.ReservationReady);
    }

    [Fact]
    public void BitcoinFamilyBrokenSendManyResolvesPerAddressSendToAddress()
    {
        var resolver = NewResolver(Coin("brokenbtc", "bitcoin", "BTC") with { HasBrokenSendMany = true });

        var result = resolver.Resolve("brokenbtc");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.SendShapes.PerAddress, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendToAddress, result.Profile.SendMethod);
        Assert.True(result.Profile.AllowsPerAddress);
        Assert.False(result.Profile.AllowsBatchMultiRecipient);
    }

    [Theory]
    [InlineData("progpow")]
    [InlineData("satoshicash")]
    public void ExplicitLegacyBitcoinOverridesResolveBitcoinRpcWithoutRewritingCoinFamily(string family)
    {
        var resolver = NewResolver(Coin(family, family, "COIN"));

        var result = resolver.Resolve(family);

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.BitcoinRpc, result.Profile.AdapterId);
        Assert.Equal(family, result.Profile.CoinFamily);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendMany, result.Profile.SendMethod);
        Assert.True(result.Profile.ReservationReady);
    }

    [Fact]
    public void EquihashBitcoinOverrideResolvesEquihashBitcoinRpc()
    {
        var resolver = NewResolver(Coin("zec-fork", "equihash", "ZF") with { UseBitcoinPayoutHandler = true });

        var result = resolver.Resolve("zec-fork");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.EquihashBitcoinRpc, result.Profile.AdapterId);
        Assert.Equal("equihash", result.Profile.CoinFamily);
        Assert.Equal(PayoutProfileConstants.SendShapes.BatchMultiRecipient, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendMany, result.Profile.SendMethod);
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.TxId, result.Profile.SettlementEvidenceKind);
        Assert.True(result.Profile.SupportsTransparentTxId);
        Assert.True(result.Profile.ReservationReady);
    }

    [Fact]
    public void EquihashBitcoinOverrideBrokenSendManyResolvesPerAddressSendToAddress()
    {
        var resolver = NewResolver(Coin("zec-fork", "equihash", "ZF") with
        {
            UseBitcoinPayoutHandler = true,
            HasBrokenSendMany = true
        });

        var result = resolver.Resolve("zec-fork");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.EquihashBitcoinRpc, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.PerAddress, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendToAddress, result.Profile.SendMethod);
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.TxId, result.Profile.SettlementEvidenceKind);
        Assert.True(result.Profile.AllowsPerAddress);
        Assert.False(result.Profile.AllowsBatchMultiRecipient);
        Assert.False(result.Profile.RequiresOperationIdProvider);
        Assert.False(result.Profile.SupportsShieldedOperationTracking);
        Assert.True(result.Profile.ReservationReady);
    }

    [Fact]
    public void EquihashAsyncProfileIsReservationReadyWithOperationTrackingLifecycle()
    {
        var resolver = NewResolver(Coin("zec", "equihash", "ZEC"));

        var result = resolver.Resolve("zec");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.EquihashZAsync, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.AsyncOperation, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.ZSendMany, result.Profile.SendMethod);
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId, result.Profile.SettlementEvidenceKind);
        Assert.True(result.Profile.RequiresOperationIdProvider);
        Assert.True(result.Profile.SupportsShieldedOperationTracking);
        Assert.True(result.Profile.AllowsBatchMultiRecipient);
        Assert.True(result.Profile.RequiresWalletDaemon);
        Assert.Equal(50, result.Profile.MaxRecipientsPerAttempt);
        Assert.True(result.Profile.ReservationReady);
        Assert.Empty(result.Profile.NotReadyReason);
    }

    [Fact]
    public void UnknownCoinFailsClosed()
    {
        var resolver = NewResolver(Coin("bitcoin", "bitcoin", "BTC"));

        var result = resolver.Resolve("missing");

        Assert.Equal(PayoutProfileResolutionStatus.Unsupported, result.Status);
        Assert.False(result.HasProfile);
        Assert.NotEmpty(result.Reason);
    }

    [Fact]
    public void UnsupportedFamilyFailsClosed()
    {
        var resolver = NewResolver(Coin("mystery", "unknown-family", "MYS"));

        var result = resolver.Resolve("mystery");

        Assert.Equal(PayoutProfileResolutionStatus.Unsupported, result.Status);
        Assert.False(result.HasProfile);
        Assert.NotEmpty(result.Reason);
    }

    [Fact]
    public void KaspaProfileIsReservationReadyWithPerAddressRealTxIdEvidence()
    {
        var resolver = NewResolver(Coin("kaspa", "kaspa", "KAS"));

        var result = resolver.Resolve("kaspa");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal("kaspa", result.Profile.CoinFamily);
        Assert.Equal(PayoutProfileConstants.AdapterIds.KaspaWalletWrapper, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.PerAddress, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.KaspaSend, result.Profile.SendMethod);
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.TxId, result.Profile.SettlementEvidenceKind);
        Assert.True(result.Profile.AllowsPerAddress);
        Assert.False(result.Profile.AllowsBatchMultiRecipient);
        Assert.True(result.Profile.RequiresExternalWalletWrapper);
        Assert.False(result.Profile.PlaceholderEvidenceUnsafe);
        Assert.True(result.Profile.ReservationReady);
        Assert.Empty(result.Profile.NotReadyReason);
    }

    [Theory]
    [InlineData("handshake")]
    [InlineData("ethereum")]
    [InlineData("ergo")]
    [InlineData("beam")]
    public void NonAsyncApprovedProfilesStillResolveAfterAsyncInvariants(string family)
    {
        var resolver = NewResolver(Coin(family, family, "COIN"));

        var result = resolver.Resolve(family);

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.True(result.Profile.ReservationReady);
    }

    [Fact]
    public void WarthogProfileMarksPrivateKeyRequirement()
    {
        var resolver = NewResolver(Coin("warthog", "warthog", "WART"));

        var result = resolver.Resolve("warthog");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.WarthogRestSigned, result.Profile.AdapterId);
        Assert.True(result.Profile.RequiresPrivateKeyMaterial);
        Assert.True(result.Profile.ReservationReady);
    }

    [Fact]
    public void WarthogProfileIsReservationReadyWithPrivateKeyBoundaryMetadata()
    {
        var resolver = NewResolver(Coin("warthog", "warthog", "WART"));

        var result = resolver.Resolve("warthog");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.True(result.HasProfile);
        Assert.Equal(PayoutProfileConstants.AdapterIds.WarthogRestSigned, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.PerAddress, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.TransactionAdd, result.Profile.SendMethod);
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.RawHash, result.Profile.SettlementEvidenceKind);
        Assert.True(result.Profile.ReservationReady);
        Assert.True(result.Profile.RequiresPrivateKeyMaterial);
        Assert.True(result.Profile.RequiresExternalWalletWrapper);
        Assert.True(result.Profile.AllowsPerAddress);
        Assert.False(result.Profile.AllowsBatchMultiRecipient);
    }

    [Fact]
    public void ResolverFailsClosedForWarthogRestSignedWithNonPerAddressSendShape()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-wart", "bad-wart", "WART") }),
            new IPayoutProfileProvider[] { new BadWarthogProvider(sendShape: PayoutProfileConstants.SendShapes.BatchMultiRecipient) });

        var result = resolver.Resolve("bad-wart");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.Contains("per_address", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForWarthogRestSignedWithoutTransactionAddMethod()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-wart", "bad-wart", "WART") }),
            new IPayoutProfileProvider[] { new BadWarthogProvider(sendMethod: "wrong-method") });

        var result = resolver.Resolve("bad-wart");

        Assert.Contains("transaction/add", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForWarthogRestSignedWithoutPrivateKeyMaterialFlag()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-wart", "bad-wart", "WART") }),
            new IPayoutProfileProvider[] { new BadWarthogProvider(requiresPrivateKeyMaterial: false) });

        var result = resolver.Resolve("bad-wart");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.Contains("RequiresPrivateKeyMaterial", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForWarthogRestSignedWithBatchMultiRecipient()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-wart", "bad-wart", "WART") }),
            new IPayoutProfileProvider[] { new BadWarthogProvider(allowsBatchMultiRecipient: true) });

        var result = resolver.Resolve("bad-wart");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.Contains("batch multi-recipient", result.Reason);
    }

    [Fact]
    public void CryptonoteProfileIsReservationReadyWithPaymentIdAwarePlanningAndSingleEvidencePolicy()
    {
        var resolver = NewResolver(Coin("cryptonote", "cryptonote", "XMR") with
        {
            RawExtensionFlags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["addressPrefixIntegrated"] = "19",
                ["addressPrefixIntegratedTestnet"] = "54",
                ["addressPrefixIntegratedStagenet"] = "25"
            }
        });

        var result = resolver.Resolve("cryptonote");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.CryptonoteWalletRpc, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.AddressGroup, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.Transfer, result.Profile.SendMethod);
        Assert.Equal(PayoutProfileConstants.PlanningPolicies.CryptonotePaymentIdAware,
            result.Profile.AttemptPlanningPolicy);
        Assert.Equal(15, result.Profile.MaxRecipientsPerAttempt);
        Assert.True(result.Profile.ReservationReady);
        Assert.True(result.Profile.MayReturnMultipleTransactionHashes);
        Assert.True(result.Profile.RequiresSingleEvidencePerAttempt);
        Assert.Equal(PayoutProfileConstants.MultiHashEvidencePolicies.Unsupported,
            result.Profile.MultiHashEvidencePolicy);
        Assert.Contains(19ul, result.Profile.IntegratedAddressPrefixes);
    }

    [Fact]
    public void ZanoProfileIsReservationReadyWithPaymentIdAwarePlanningAndSingleEvidencePolicy()
    {
        var resolver = NewResolver(Coin("zano", "zano", "ZANO") with
        {
            RawExtensionFlags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["addressPrefixIntegrated"] = "13944",
                ["addressPrefixIntegratedTestnet"] = "13944",
                ["addressV2PrefixIntegrated"] = "14072",
                ["addressV2PrefixIntegratedTestnet"] = "14072",
                ["auditableAddressIntegratedPrefix"] = "35401",
                ["auditableAddressIntegratedPrefixTestnet"] = "35401"
            }
        });

        var result = resolver.Resolve("zano");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.ZanoWalletRpc, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.AddressGroup, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.Transfer, result.Profile.SendMethod);
        Assert.Equal(PayoutProfileConstants.PlanningPolicies.ZanoPaymentIdAware,
            result.Profile.AttemptPlanningPolicy);
        Assert.Equal(256, result.Profile.MaxRecipientsPerAttempt);
        Assert.True(result.Profile.ReservationReady);
        Assert.True(result.Profile.MayReturnMultipleTransactionHashes);
        Assert.True(result.Profile.RequiresSingleEvidencePerAttempt);
        Assert.Equal(PayoutProfileConstants.MultiHashEvidencePolicies.Unsupported,
            result.Profile.MultiHashEvidencePolicy);
        Assert.Contains(13944ul, result.Profile.IntegratedAddressPrefixes);
        Assert.Contains(35401ul, result.Profile.IntegratedAddressPrefixes);
    }

    [Theory]
    [InlineData("cryptonote")]
    [InlineData("zano")]
    public void PaymentIdAwareProfilesFailClosedWithoutIntegratedPrefixMetadata(string family)
    {
        var resolver = NewResolver(Coin(family, family, "COIN"));

        var result = resolver.Resolve(family);

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("integrated address prefix", result.Reason);
    }

    [Fact]
    public void XelisProfileIsReservationReadyWithLegacyMaxRecipientLimit()
    {
        var resolver = NewResolver(Coin("xelis", "xelis", "XEL"));

        var result = resolver.Resolve("xelis");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.XelisWalletRpc, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.AddressGroup, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.BuildTransaction, result.Profile.SendMethod);
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.RawHash, result.Profile.SettlementEvidenceKind);
        Assert.True(result.Profile.RequiresWalletDaemon);
        Assert.True(result.Profile.AllowsBatchMultiRecipient);
        Assert.True(result.Profile.ReservationReady);
        Assert.Equal(255, result.Profile.MaxRecipientsPerAttempt);
    }

    [Fact]
    public void ConcealAddressGroupProfileRemainsReservationReadyWithMaxRecipientLimit()
    {
        var resolver = NewResolver(Coin("conceal", "conceal", "CCX") with
        {
            RawExtensionFlags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["addressPrefixIntegrated"] = "31444",
                ["addressPrefixIntegratedTestnet"] = "31444"
            }
        });

        var result = resolver.Resolve("conceal");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.SendShapes.AddressGroup, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.PlanningPolicies.ConcealPaymentIdAware,
            result.Profile.AttemptPlanningPolicy);
        Assert.True(result.Profile.ReservationReady);
        Assert.Equal(15, result.Profile.MaxRecipientsPerAttempt);
        Assert.Contains(31444ul, result.Profile.IntegratedAddressPrefixes);
    }

    [Fact]
    public void ResolverFailsClosedForReservationReadyAddressGroupWithoutMaxRecipientLimit()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-group", "bad-group", "BAD") }),
            new[] { new BadAddressGroupProvider() });

        var result = resolver.Resolve("bad-group");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("MaxRecipientsPerAttempt", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForReadyMultiHashProfileMissingRequiresSingleEvidence()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-multihash", "bad-multihash", "BAD") }),
            new[] { new BadMultiHashProvider(
                requiresSingleEvidence: false,
                multiHashPolicy: PayoutProfileConstants.MultiHashEvidencePolicies.Unsupported) });

        var result = resolver.Resolve("bad-multihash");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("RequiresSingleEvidencePerAttempt", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForReadyMultiHashProfileWithNotApplicableEvidencePolicy()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-multihash", "bad-multihash", "BAD") }),
            new[] { new BadMultiHashProvider(
                requiresSingleEvidence: true,
                multiHashPolicy: PayoutProfileConstants.MultiHashEvidencePolicies.NotApplicable) });

        var result = resolver.Resolve("bad-multihash");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("MultiHashEvidencePolicy", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForReadyProfileWithUnsafePlaceholderEvidenceKind()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-evidence", "bad-evidence", "BAD") }),
            new[] { new BadEvidenceProvider(PayoutProfileConstants.SettlementEvidenceKinds.UnsafePlaceholder) });

        var result = resolver.Resolve("bad-evidence");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("placeholder", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForReadyProfileWithPlaceholderEvidenceUnsafeFlag()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-evidence", "bad-evidence", "BAD") }),
            new[] { new BadEvidenceProvider(PayoutProfileConstants.SettlementEvidenceKinds.TxId,
                placeholderEvidenceUnsafe: true) });

        var result = resolver.Resolve("bad-evidence");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("placeholder", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForReadyProfileWithMissingSettlementEvidenceKind()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-evidence", "bad-evidence", "BAD") }),
            new[] { new BadEvidenceProvider(" ") });

        var result = resolver.Resolve("bad-evidence");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("settlement evidence", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForReadyProfileWithFakePlaceholderEvidenceSemantics()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-evidence", "bad-evidence", "BAD") }),
            new[] { new BadEvidenceProvider("send:{to}:{amount}") });

        var result = resolver.Resolve("bad-evidence");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("placeholder", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForReadyProfileWithOperationIdAsFinalEvidence()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-evidence", "bad-evidence", "BAD") }),
            new[] { new BadEvidenceProvider(PayoutProfileConstants.SettlementEvidenceKinds.OperationId) });

        var result = resolver.Resolve("bad-evidence");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("operation ids", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForAsyncOperationWithoutOperationIdProvider()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-async", "bad-async", "BAD") }),
            new[] { new BadAsyncOperationProvider(requiresOperationIdProvider: false) });

        var result = resolver.Resolve("bad-async");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("operation-id provider", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForAsyncOperationWithoutShieldedTracking()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-async", "bad-async", "BAD") }),
            new[] { new BadAsyncOperationProvider(supportsShieldedOperationTracking: false) });

        var result = resolver.Resolve("bad-async");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("shielded tracking", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForAsyncOperationWithTxIdOnlyEvidence()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-async", "bad-async", "BAD") }),
            new[] { new BadAsyncOperationProvider(
                settlementEvidenceKind: PayoutProfileConstants.SettlementEvidenceKinds.TxId) });

        var result = resolver.Resolve("bad-async");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains(PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId, result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForAsyncOperationWithoutMaxRecipientLimit()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-async", "bad-async", "BAD") }),
            new[] { new BadAsyncOperationProvider(maxRecipientsPerAttempt: 0) });

        var result = resolver.Resolve("bad-async");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("MaxRecipientsPerAttempt", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForOperationIdThenTxIdOnNonAsyncSendShape()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-async", "bad-async", "BAD") }),
            new[] { new BadAsyncOperationProvider(sendShape: PayoutProfileConstants.SendShapes.BatchMultiRecipient) });

        var result = resolver.Resolve("bad-async");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("async_operation", result.Reason);
    }

    [Fact]
    public void AlephiumProfileIsReservationReadyWithGroupAwarePlanning()
    {
        var resolver = NewResolver(Coin("alephium", "alephium", "ALPH"));

        var result = resolver.Resolve("alephium");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.AlephiumWalletApi, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SendShapes.AddressGroup, result.Profile.SendShape);
        Assert.Equal(PayoutProfileConstants.SendMethods.BuildSignSubmit, result.Profile.SendMethod);
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.TxId, result.Profile.SettlementEvidenceKind);
        Assert.Equal(PayoutProfileConstants.PlanningPolicies.AlephiumGroupAware,
            result.Profile.AttemptPlanningPolicy);
        Assert.True(result.Profile.SupportsTransparentTxId);
        Assert.True(result.Profile.AllowsBatchMultiRecipient);
        Assert.True(result.Profile.RequiresWalletDaemon);
        Assert.Equal(4, result.Profile.AddressGroupCount);
        Assert.True(result.Profile.MaxRecipientsPerAttempt > 0);
        Assert.True(result.Profile.ReservationReady);
        Assert.Empty(result.Profile.NotReadyReason);
    }

    [Fact]
    public void ResolverFailsClosedForAlephiumGroupAwarePolicyWithoutAddressGroupCount()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-alph", "bad-alph", "BAD") }),
            new[] { new BadAlephiumGroupAwareProvider(addressGroupCount: null, maxRecipientsPerAttempt: 64) });

        var result = resolver.Resolve("bad-alph");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("AddressGroupCount", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForAlephiumGroupAwarePolicyWithWrongAddressGroupCount()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-alph", "bad-alph", "BAD") }),
            new[] { new BadAlephiumGroupAwareProvider(addressGroupCount: 2, maxRecipientsPerAttempt: 64) });

        var result = resolver.Resolve("bad-alph");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("AddressGroupCount", result.Reason);
    }

    [Fact]
    public void ResolverFailsClosedForAlephiumGroupAwarePolicyWithoutMaxRecipientsPerAttempt()
    {
        var resolver = new PayoutProfileResolver(
            new TestCoinMetadataRegistry(new[] { Coin("bad-alph", "bad-alph", "BAD") }),
            new[] { new BadAlephiumGroupAwareProvider(addressGroupCount: 4, maxRecipientsPerAttempt: 0) });

        var result = resolver.Resolve("bad-alph");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.False(result.Profile.ReservationReady);
        Assert.Contains("MaxRecipientsPerAttempt", result.Reason);
    }

    [Fact]
    public void ResolverNeverReturnsNullDefaultProfile()
    {
        var resolver = NewResolver(Coin("ergo", "ergo", "ERG"));

        var result = resolver.Resolve("ergo");

        Assert.Equal(PayoutProfileResolutionStatus.Resolved, result.Status);
        Assert.True(result.HasProfile);
        Assert.NotNull(result.Profile);
        Assert.NotEmpty(result.Profile.AdapterId);
        Assert.NotEmpty(result.Profile.SendShape);
        Assert.NotEmpty(result.Profile.SendMethod);
    }

    private static IPayoutProfileResolver NewResolver(params CoinDescriptor[] coins)
    {
        return new PayoutProfileResolver(new TestCoinMetadataRegistry(coins), NewProviders());
    }

    private static CoinDescriptor Coin(string key, string family, string symbol)
    {
        return new CoinDescriptor
        {
            CoinKey = key,
            Name = key,
            CanonicalName = key,
            Symbol = symbol,
            Family = family
        };
    }

    private static IPayoutProfileProvider[] NewProviders()
    {
        return new IPayoutProfileProvider[]
        {
            new BitcoinPayoutProfileProvider(),
            new HandshakePayoutProfileProvider(),
            new EquihashPayoutProfileProvider(),
            new CryptonotePayoutProfileProvider(),
            new ConcealPayoutProfileProvider(),
            new ZanoPayoutProfileProvider(),
            new EthereumPayoutProfileProvider(),
            new ErgoPayoutProfileProvider(),
            new BeamPayoutProfileProvider(),
            new AlephiumPayoutProfileProvider(),
            new KaspaPayoutProfileProvider(),
            new XelisPayoutProfileProvider(),
            new WarthogPayoutProfileProvider()
        };
    }

    private class TestCoinMetadataRegistry : ICoinMetadataRegistry
    {
        public TestCoinMetadataRegistry(IEnumerable<CoinDescriptor> coins)
        {
            this.coins = coins.ToDictionary(x => x.CoinKey, StringComparer.OrdinalIgnoreCase);
        }

        private readonly IReadOnlyDictionary<string, CoinDescriptor> coins;

        public bool TryGetCoin(string coinKey, out CoinDescriptor descriptor)
        {
            return coins.TryGetValue(coinKey, out descriptor!);
        }
    }

    private class BadAddressGroupProvider : IPayoutProfileProvider
    {
        public bool CanResolve(CoinDescriptor coin)
        {
            return string.Equals(coin.Family, "bad-group", StringComparison.Ordinal);
        }

        public PayoutProfileResolution Resolve(CoinDescriptor coin)
        {
            return PayoutProfileResolution.Resolved(new PayoutProfile
            {
                CoinKey = coin.CoinKey,
                CoinSymbol = coin.Symbol,
                CoinFamily = coin.Family,
                AdapterId = "bad-group-adapter",
                SendShape = PayoutProfileConstants.SendShapes.AddressGroup,
                SendMethod = "bad-send",
                SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
                ReservationReady = true
            });
        }
    }

    private class BadAlephiumGroupAwareProvider : IPayoutProfileProvider
    {
        private readonly int? addressGroupCount;
        private readonly int maxRecipientsPerAttempt;

        public BadAlephiumGroupAwareProvider(int? addressGroupCount, int maxRecipientsPerAttempt)
        {
            this.addressGroupCount = addressGroupCount;
            this.maxRecipientsPerAttempt = maxRecipientsPerAttempt;
        }

        public bool CanResolve(CoinDescriptor coin)
        {
            return string.Equals(coin.Family, "bad-alph", StringComparison.Ordinal);
        }

        public PayoutProfileResolution Resolve(CoinDescriptor coin)
        {
            return PayoutProfileResolution.Resolved(new PayoutProfile
            {
                CoinKey = coin.CoinKey,
                CoinSymbol = coin.Symbol,
                CoinFamily = coin.Family,
                AdapterId = "bad-alph-adapter",
                SendShape = PayoutProfileConstants.SendShapes.AddressGroup,
                SendMethod = PayoutProfileConstants.SendMethods.BuildSignSubmit,
                SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.TxId,
                AttemptPlanningPolicy = PayoutProfileConstants.PlanningPolicies.AlephiumGroupAware,
                MaxRecipientsPerAttempt = maxRecipientsPerAttempt,
                AddressGroupCount = addressGroupCount,
                ReservationReady = true
            });
        }
    }

    private class BadWarthogProvider : IPayoutProfileProvider
    {
        private readonly string sendShape;
        private readonly string sendMethod;
        private readonly bool requiresPrivateKeyMaterial;
        private readonly bool allowsBatchMultiRecipient;

        public BadWarthogProvider(
            string sendShape = PayoutProfileConstants.SendShapes.PerAddress,
            string sendMethod = PayoutProfileConstants.SendMethods.TransactionAdd,
            bool requiresPrivateKeyMaterial = true,
            bool allowsBatchMultiRecipient = false)
        {
            this.sendShape = sendShape;
            this.sendMethod = sendMethod;
            this.requiresPrivateKeyMaterial = requiresPrivateKeyMaterial;
            this.allowsBatchMultiRecipient = allowsBatchMultiRecipient;
        }

        public bool CanResolve(CoinDescriptor coin)
        {
            return string.Equals(coin.Family, "bad-wart", StringComparison.Ordinal);
        }

        public PayoutProfileResolution Resolve(CoinDescriptor coin)
        {
            return PayoutProfileResolution.Resolved(new PayoutProfile
            {
                CoinKey = coin.CoinKey,
                CoinSymbol = coin.Symbol,
                CoinFamily = coin.Family,
                AdapterId = PayoutProfileConstants.AdapterIds.WarthogRestSigned,
                SendShape = sendShape,
                SendMethod = sendMethod,
                SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.RawHash,
                AllowsPerAddress = true,
                AllowsBatchMultiRecipient = allowsBatchMultiRecipient,
                RequiresPrivateKeyMaterial = requiresPrivateKeyMaterial,
                RequiresExternalWalletWrapper = true,
                ReservationReady = true
            });
        }
    }

    private class BadMultiHashProvider : IPayoutProfileProvider
    {
        private readonly bool requiresSingleEvidence;
        private readonly string multiHashPolicy;

        public BadMultiHashProvider(bool requiresSingleEvidence, string multiHashPolicy)
        {
            this.requiresSingleEvidence = requiresSingleEvidence;
            this.multiHashPolicy = multiHashPolicy;
        }

        public bool CanResolve(CoinDescriptor coin)
        {
            return string.Equals(coin.Family, "bad-multihash", StringComparison.Ordinal);
        }

        public PayoutProfileResolution Resolve(CoinDescriptor coin)
        {
            return PayoutProfileResolution.Resolved(new PayoutProfile
            {
                CoinKey = coin.CoinKey,
                CoinSymbol = coin.Symbol,
                CoinFamily = coin.Family,
                AdapterId = "bad-multihash-adapter",
                SendShape = PayoutProfileConstants.SendShapes.AddressGroup,
                SendMethod = PayoutProfileConstants.SendMethods.Transfer,
                SettlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.RawHash,
                MaxRecipientsPerAttempt = 15,
                MayReturnMultipleTransactionHashes = true,
                RequiresSingleEvidencePerAttempt = requiresSingleEvidence,
                MultiHashEvidencePolicy = multiHashPolicy,
                ReservationReady = true
            });
        }
    }

    private class BadEvidenceProvider : IPayoutProfileProvider
    {
        private readonly string settlementEvidenceKind;
        private readonly bool placeholderEvidenceUnsafe;

        public BadEvidenceProvider(string settlementEvidenceKind, bool placeholderEvidenceUnsafe = false)
        {
            this.settlementEvidenceKind = settlementEvidenceKind;
            this.placeholderEvidenceUnsafe = placeholderEvidenceUnsafe;
        }

        public bool CanResolve(CoinDescriptor coin)
        {
            return string.Equals(coin.Family, "bad-evidence", StringComparison.Ordinal);
        }

        public PayoutProfileResolution Resolve(CoinDescriptor coin)
        {
            return PayoutProfileResolution.Resolved(new PayoutProfile
            {
                CoinKey = coin.CoinKey,
                CoinSymbol = coin.Symbol,
                CoinFamily = coin.Family,
                AdapterId = "bad-evidence-adapter",
                SendShape = PayoutProfileConstants.SendShapes.PerAddress,
                SendMethod = "bad-send",
                SettlementEvidenceKind = settlementEvidenceKind,
                PlaceholderEvidenceUnsafe = placeholderEvidenceUnsafe,
                ReservationReady = true
            });
        }
    }

    private class BadAsyncOperationProvider : IPayoutProfileProvider
    {
        private readonly bool requiresOperationIdProvider;
        private readonly bool supportsShieldedOperationTracking;
        private readonly string settlementEvidenceKind;
        private readonly string sendShape;
        private readonly int maxRecipientsPerAttempt;

        public BadAsyncOperationProvider(
            bool requiresOperationIdProvider = true,
            bool supportsShieldedOperationTracking = true,
            string settlementEvidenceKind = PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId,
            string sendShape = PayoutProfileConstants.SendShapes.AsyncOperation,
            int maxRecipientsPerAttempt = 50)
        {
            this.requiresOperationIdProvider = requiresOperationIdProvider;
            this.supportsShieldedOperationTracking = supportsShieldedOperationTracking;
            this.settlementEvidenceKind = settlementEvidenceKind;
            this.sendShape = sendShape;
            this.maxRecipientsPerAttempt = maxRecipientsPerAttempt;
        }

        public bool CanResolve(CoinDescriptor coin)
        {
            return string.Equals(coin.Family, "bad-async", StringComparison.Ordinal);
        }

        public PayoutProfileResolution Resolve(CoinDescriptor coin)
        {
            return PayoutProfileResolution.Resolved(new PayoutProfile
            {
                CoinKey = coin.CoinKey,
                CoinSymbol = coin.Symbol,
                CoinFamily = coin.Family,
                AdapterId = "bad-async-adapter",
                SendShape = sendShape,
                SendMethod = PayoutProfileConstants.SendMethods.ZSendMany,
                SettlementEvidenceKind = settlementEvidenceKind,
                RequiresOperationIdProvider = requiresOperationIdProvider,
                SupportsShieldedOperationTracking = supportsShieldedOperationTracking,
                MaxRecipientsPerAttempt = maxRecipientsPerAttempt,
                ReservationReady = true
            });
        }
    }
}
