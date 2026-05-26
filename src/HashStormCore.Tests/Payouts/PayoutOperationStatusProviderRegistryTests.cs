using System;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class PayoutOperationStatusProviderRegistryTests
{
    [Fact]
    public void TryGetProvider_ExactProfileKeyResolvesProvider()
    {
        var provider = new TestProvider();
        var registry = NewRegistry(provider);

        var resolved = registry.TryGetProvider(MatchingProfile(), out var actual);

        Assert.True(resolved);
        Assert.Same(provider, actual);
    }

    [Fact]
    public void TryGetProvider_WrongCoinFamilyDoesNotResolve()
    {
        var registry = NewRegistry(new TestProvider());

        var resolved = registry.TryGetProvider(MatchingProfile() with { CoinFamily = "other-family" }, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryGetProvider_WrongAdapterIdDoesNotResolve()
    {
        var registry = NewRegistry(new TestProvider());

        var resolved = registry.TryGetProvider(MatchingProfile() with { AdapterId = "other-adapter" }, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryGetProvider_WrongSendShapeDoesNotResolve()
    {
        var registry = NewRegistry(new TestProvider());

        var resolved = registry.TryGetProvider(MatchingProfile() with { SendShape = "other-shape" }, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryGetProvider_WrongSendMethodDoesNotResolve()
    {
        var registry = NewRegistry(new TestProvider());

        var resolved = registry.TryGetProvider(MatchingProfile() with { SendMethod = "other-method" }, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryGetProvider_DoesNotFallbackToPartialFamilyMatch()
    {
        var provider = new TestProvider();
        var registry = new PayoutOperationStatusProviderRegistry(new[]
        {
            new PayoutOperationStatusProviderRegistration
            {
                Key = MatchingKey() with { AdapterId = "adapter-a" },
                Provider = provider
            }
        });

        var resolved = registry.TryGetProvider(MatchingProfile() with { AdapterId = "adapter-b" }, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void Constructor_DuplicateRegistrationThrows()
    {
        var key = MatchingKey();

        Assert.Throws<ArgumentException>(() => new PayoutOperationStatusProviderRegistry(new[]
        {
            new PayoutOperationStatusProviderRegistration { Key = key, Provider = new TestProvider() },
            new PayoutOperationStatusProviderRegistration { Key = key, Provider = new TestProvider() }
        }));
    }

    [Fact]
    public void Constructor_NullProviderThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new PayoutOperationStatusProviderRegistry(new[]
        {
            new PayoutOperationStatusProviderRegistration { Key = MatchingKey(), Provider = null }
        }));
    }

    [Fact]
    public void Constructor_NullKeyThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new PayoutOperationStatusProviderRegistry(new[]
        {
            new PayoutOperationStatusProviderRegistration { Key = null, Provider = new TestProvider() }
        }));
    }

    [Theory]
    [InlineData("CoinFamily")]
    [InlineData("AdapterId")]
    [InlineData("SendShape")]
    [InlineData("SendMethod")]
    public void Constructor_EmptyKeyFieldThrows(string field)
    {
        var key = field switch
        {
            "CoinFamily" => MatchingKey() with { CoinFamily = " " },
            "AdapterId" => MatchingKey() with { AdapterId = " " },
            "SendShape" => MatchingKey() with { SendShape = " " },
            "SendMethod" => MatchingKey() with { SendMethod = " " },
            _ => MatchingKey()
        };

        Assert.Throws<ArgumentException>(() => new PayoutOperationStatusProviderRegistry(new[]
        {
            new PayoutOperationStatusProviderRegistration { Key = key, Provider = new TestProvider() }
        }));
    }

    [Theory]
    [InlineData("CoinFamily")]
    [InlineData("AdapterId")]
    [InlineData("SendShape")]
    [InlineData("SendMethod")]
    public void Constructor_NullKeyFieldThrows(string field)
    {
        var key = field switch
        {
            "CoinFamily" => MatchingKey() with { CoinFamily = null },
            "AdapterId" => MatchingKey() with { AdapterId = null },
            "SendShape" => MatchingKey() with { SendShape = null },
            "SendMethod" => MatchingKey() with { SendMethod = null },
            _ => MatchingKey()
        };

        Assert.Throws<ArgumentException>(() => new PayoutOperationStatusProviderRegistry(new[]
        {
            new PayoutOperationStatusProviderRegistration { Key = key, Provider = new TestProvider() }
        }));
    }

    [Theory]
    [InlineData("CoinFamily")]
    [InlineData("AdapterId")]
    [InlineData("SendShape")]
    [InlineData("SendMethod")]
    public void TryGetProvider_ProfileWithMissingKeyFieldDoesNotResolve(string field)
    {
        var registry = NewRegistry(new TestProvider());
        var profile = field switch
        {
            "CoinFamily" => MatchingProfile() with { CoinFamily = " " },
            "AdapterId" => MatchingProfile() with { AdapterId = " " },
            "SendShape" => MatchingProfile() with { SendShape = " " },
            "SendMethod" => MatchingProfile() with { SendMethod = " " },
            _ => MatchingProfile()
        };

        var resolved = registry.TryGetProvider(profile, out _);

        Assert.False(resolved);
    }

    [Theory]
    [InlineData("CoinFamily")]
    [InlineData("AdapterId")]
    [InlineData("SendShape")]
    [InlineData("SendMethod")]
    public void TryGetProvider_ProfileWithNullKeyFieldDoesNotResolve(string field)
    {
        var registry = NewRegistry(new TestProvider());
        var profile = field switch
        {
            "CoinFamily" => MatchingProfile() with { CoinFamily = null },
            "AdapterId" => MatchingProfile() with { AdapterId = null },
            "SendShape" => MatchingProfile() with { SendShape = null },
            "SendMethod" => MatchingProfile() with { SendMethod = null },
            _ => MatchingProfile()
        };

        var resolved = registry.TryGetProvider(profile, out _);

        Assert.False(resolved);
    }

    private static PayoutOperationStatusProviderRegistry NewRegistry(IPayoutOperationStatusProvider provider)
    {
        return new PayoutOperationStatusProviderRegistry(new[]
        {
            new PayoutOperationStatusProviderRegistration
            {
                Key = MatchingKey(),
                Provider = provider
            }
        });
    }

    private static PayoutOperationStatusProviderKey MatchingKey()
    {
        return new PayoutOperationStatusProviderKey
        {
            CoinFamily = "test-family",
            AdapterId = "test-adapter",
            SendShape = "test-shape",
            SendMethod = "test-method"
        };
    }

    private static PayoutProfile MatchingProfile()
    {
        return new PayoutProfile
        {
            CoinFamily = "test-family",
            AdapterId = "test-adapter",
            SendShape = "test-shape",
            SendMethod = "test-method",
            ReservationReady = true
        };
    }

    private class TestProvider : IPayoutOperationStatusProvider
    {
        public Task<PayoutOperationStatusResult> GetOperationStatusAsync(PayoutReconciliationAttemptSummary attempt,
            string operationId, CancellationToken ct)
        {
            return Task.FromResult(PayoutOperationStatusResult.Pending());
        }
    }
}
