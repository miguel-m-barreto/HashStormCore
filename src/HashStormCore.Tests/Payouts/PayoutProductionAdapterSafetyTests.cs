using System;
using System.Linq;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class PayoutProductionAdapterSafetyTests
{
    [Fact]
    public void PayoutsAssemblyContainsNoProductionAttemptSenderImplementations()
    {
        var implementations = typeof(IPayoutAttemptSender).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && !x.IsInterface)
            .Where(x => typeof(IPayoutAttemptSender).IsAssignableFrom(x))
            .ToArray();

        Assert.Empty(implementations);
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
    public void EmptyProductionStyleRegistriesDoNotResolveReadyProfiles()
    {
        var profile = new PayoutProfile
        {
            CoinFamily = "test-family",
            AdapterId = "test-adapter",
            SendShape = PayoutSendShapes.BatchMultiRecipient,
            SendMethod = "test-method",
            ReservationReady = true
        };
        var senderRegistry = new PayoutAttemptSenderRegistry(Array.Empty<PayoutAttemptSenderRegistration>());
        var providerRegistry = new PayoutOperationStatusProviderRegistry(
            Array.Empty<PayoutOperationStatusProviderRegistration>());

        Assert.False(senderRegistry.TryGetSender(profile, out _));
        Assert.False(providerRegistry.TryGetProvider(profile, out _));
    }
}
