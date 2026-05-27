using System;
using System.Linq;
using HashStormCore.Payments;
using HashStormCore.Payouts.Bitcoin;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class PayoutProductionAdapterSafetyTests
{
    [Fact]
    public void PayoutsAssemblyContainsOnlyKnownUnregisteredAttemptSenderImplementations()
    {
        var implementations = typeof(IPayoutAttemptSender).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && !x.IsInterface)
            .Where(x => typeof(IPayoutAttemptSender).IsAssignableFrom(x))
            .Select(x => x.FullName)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { typeof(BitcoinRpcPayoutSender).FullName }, implementations);
    }

    [Fact]
    public void PayoutsAssemblyContainsNoProductionOperationStatusProviderImplementations()
    {
        var implementations = typeof(IPayoutOperationStatusProvider).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && !x.IsInterface)
            .Where(x => typeof(IPayoutOperationStatusProvider).IsAssignableFrom(x))
            .ToArray();

        Assert.Empty(implementations);
    }

    [Fact]
    public void PayoutsAssemblyContainsNoProductionBitcoinRpcClientImplementations()
    {
        var implementations = typeof(IBitcoinPayoutRpcClient).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && !x.IsInterface)
            .Where(x => typeof(IBitcoinPayoutRpcClient).IsAssignableFrom(x))
            .ToArray();

        Assert.Empty(implementations);
    }

    [Fact]
    public void EmptyProductionStyleRegistriesDoNotResolveReadyProfiles()
    {
        var profile = new PayoutProfile
        {
            CoinFamily = PayoutProfileConstants.Families.Bitcoin,
            AdapterId = PayoutProfileConstants.AdapterIds.BitcoinRpc,
            SendShape = PayoutProfileConstants.SendShapes.BatchMultiRecipient,
            SendMethod = PayoutProfileConstants.SendMethods.SendMany,
            ReservationReady = true
        };
        var senderRegistry = new PayoutAttemptSenderRegistry(Array.Empty<PayoutAttemptSenderRegistration>());
        var providerRegistry = new PayoutOperationStatusProviderRegistry(
            Array.Empty<PayoutOperationStatusProviderRegistration>());

        Assert.False(senderRegistry.TryGetSender(profile, out _));
        Assert.False(providerRegistry.TryGetProvider(profile, out _));
    }
}
