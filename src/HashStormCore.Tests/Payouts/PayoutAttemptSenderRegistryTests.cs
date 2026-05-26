using System;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Payments;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class PayoutAttemptSenderRegistryTests
{
    [Fact]
    public void TryGetSender_ExactProfileKeyResolvesSender()
    {
        var sender = new TestSender();
        var registry = NewRegistry(sender);

        var resolved = registry.TryGetSender(MatchingProfile(), out var actual);

        Assert.True(resolved);
        Assert.Same(sender, actual);
    }

    [Fact]
    public void TryGetSender_WrongAdapterIdDoesNotResolve()
    {
        var registry = NewRegistry(new TestSender());

        var resolved = registry.TryGetSender(MatchingProfile() with { AdapterId = "other-adapter" }, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryGetSender_WrongSendMethodDoesNotResolve()
    {
        var registry = NewRegistry(new TestSender());

        var resolved = registry.TryGetSender(MatchingProfile() with { SendMethod = "other-method" }, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryGetSender_WrongSendShapeDoesNotResolve()
    {
        var registry = NewRegistry(new TestSender());

        var resolved = registry.TryGetSender(MatchingProfile() with { SendShape = "other-shape" }, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryGetSender_WrongCoinFamilyDoesNotResolve()
    {
        var registry = NewRegistry(new TestSender());

        var resolved = registry.TryGetSender(MatchingProfile() with { CoinFamily = "other-family" }, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryGetSender_DoesNotFallbackToPartialFamilyMatch()
    {
        var sender = new TestSender();
        var registry = new PayoutAttemptSenderRegistry(new[]
        {
            new PayoutAttemptSenderRegistration
            {
                Key = MatchingKey() with { AdapterId = "adapter-a" },
                Sender = sender
            }
        });

        var resolved = registry.TryGetSender(MatchingProfile() with { AdapterId = "adapter-b" }, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void Constructor_DuplicateRegistrationThrows()
    {
        var key = MatchingKey();

        Assert.Throws<ArgumentException>(() => new PayoutAttemptSenderRegistry(new[]
        {
            new PayoutAttemptSenderRegistration { Key = key, Sender = new TestSender() },
            new PayoutAttemptSenderRegistration { Key = key, Sender = new TestSender() }
        }));
    }

    [Fact]
    public void Constructor_NullSenderThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new PayoutAttemptSenderRegistry(new[]
        {
            new PayoutAttemptSenderRegistration { Key = MatchingKey(), Sender = null }
        }));
    }

    [Fact]
    public void Constructor_NullKeyThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new PayoutAttemptSenderRegistry(new[]
        {
            new PayoutAttemptSenderRegistration { Key = null, Sender = new TestSender() }
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

        Assert.Throws<ArgumentException>(() => new PayoutAttemptSenderRegistry(new[]
        {
            new PayoutAttemptSenderRegistration { Key = key, Sender = new TestSender() }
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

        Assert.Throws<ArgumentException>(() => new PayoutAttemptSenderRegistry(new[]
        {
            new PayoutAttemptSenderRegistration { Key = key, Sender = new TestSender() }
        }));
    }

    [Theory]
    [InlineData("CoinFamily")]
    [InlineData("AdapterId")]
    [InlineData("SendShape")]
    [InlineData("SendMethod")]
    public void TryGetSender_ProfileWithMissingKeyFieldDoesNotResolve(string field)
    {
        var registry = NewRegistry(new TestSender());
        var profile = field switch
        {
            "CoinFamily" => MatchingProfile() with { CoinFamily = " " },
            "AdapterId" => MatchingProfile() with { AdapterId = " " },
            "SendShape" => MatchingProfile() with { SendShape = " " },
            "SendMethod" => MatchingProfile() with { SendMethod = " " },
            _ => MatchingProfile()
        };

        var resolved = registry.TryGetSender(profile, out _);

        Assert.False(resolved);
    }

    [Theory]
    [InlineData("CoinFamily")]
    [InlineData("AdapterId")]
    [InlineData("SendShape")]
    [InlineData("SendMethod")]
    public void TryGetSender_ProfileWithNullKeyFieldDoesNotResolve(string field)
    {
        var registry = NewRegistry(new TestSender());
        var profile = field switch
        {
            "CoinFamily" => MatchingProfile() with { CoinFamily = null },
            "AdapterId" => MatchingProfile() with { AdapterId = null },
            "SendShape" => MatchingProfile() with { SendShape = null },
            "SendMethod" => MatchingProfile() with { SendMethod = null },
            _ => MatchingProfile()
        };

        var resolved = registry.TryGetSender(profile, out _);

        Assert.False(resolved);
    }

    private static PayoutAttemptSenderRegistry NewRegistry(IPayoutAttemptSender sender)
    {
        return new PayoutAttemptSenderRegistry(new[]
        {
            new PayoutAttemptSenderRegistration
            {
                Key = MatchingKey(),
                Sender = sender
            }
        });
    }

    private static PayoutAttemptSenderKey MatchingKey()
    {
        return new PayoutAttemptSenderKey
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

    private class TestSender : IPayoutAttemptSender
    {
        public Task<PayoutAttemptSendResult> SendAsync(PayoutSendExecutionContext context, CancellationToken ct)
        {
            return Task.FromResult(PayoutAttemptSendResult.Ambiguous("not_used", "not used"));
        }
    }
}
