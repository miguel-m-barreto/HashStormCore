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
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.TxId, result.Profile.SettlementEvidenceKind);
        Assert.True(result.Profile.ReservationReady);
    }

    [Fact]
    public void EquihashAsyncProfileIsNotReservationReady()
    {
        var resolver = NewResolver(Coin("zec", "equihash", "ZEC"));

        var result = resolver.Resolve("zec");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.EquihashZAsync, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.OperationIdThenTxId, result.Profile.SettlementEvidenceKind);
        Assert.True(result.Profile.RequiresOperationIdProvider);
        Assert.False(result.Profile.ReservationReady);
        Assert.NotEmpty(result.Profile.NotReadyReason);
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
    public void KaspaProfileIsNotReservationReadyWhenPlaceholderEvidenceIsUnsafe()
    {
        var resolver = NewResolver(Coin("kaspa", "kaspa", "KAS"));

        var result = resolver.Resolve("kaspa");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.KaspaWalletWrapper, result.Profile.AdapterId);
        Assert.Equal(PayoutProfileConstants.SettlementEvidenceKinds.UnsafePlaceholder,
            result.Profile.SettlementEvidenceKind);
        Assert.True(result.Profile.PlaceholderEvidenceUnsafe);
        Assert.False(result.Profile.ReservationReady);
    }

    [Fact]
    public void WarthogProfileMarksPrivateKeyRequirement()
    {
        var resolver = NewResolver(Coin("warthog", "warthog", "WART"));

        var result = resolver.Resolve("warthog");

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.Equal(PayoutProfileConstants.AdapterIds.WarthogRestSigned, result.Profile.AdapterId);
        Assert.True(result.Profile.RequiresPrivateKeyMaterial);
        Assert.False(result.Profile.ReservationReady);
    }

    [Theory]
    [InlineData("cryptonote", PayoutProfileConstants.AdapterIds.CryptonoteWalletRpc)]
    [InlineData("zano", PayoutProfileConstants.AdapterIds.ZanoWalletRpc)]
    public void SplitRiskProfilesRequirePerIntentEvidenceMapping(string family, string adapterId)
    {
        var resolver = NewResolver(Coin(family, family, "COIN"));

        var result = resolver.Resolve(family);

        Assert.Equal(PayoutProfileResolutionStatus.NotReady, result.Status);
        Assert.Equal(adapterId, result.Profile.AdapterId);
        Assert.True(result.Profile.MayReturnMultipleTransactionHashes);
        Assert.True(result.Profile.RequiresPerIntentEvidenceMapping);
        Assert.False(result.Profile.ReservationReady);
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
}
