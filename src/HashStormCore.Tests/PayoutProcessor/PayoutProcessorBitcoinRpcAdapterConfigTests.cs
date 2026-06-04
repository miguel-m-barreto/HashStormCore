using System;
using HashStormCore.PayoutProcessor.Configuration;
using Xunit;

namespace HashStormCore.Tests.PayoutProcessor;

public class PayoutProcessorBitcoinRpcAdapterConfigTests
{
    [Fact]
    public void DefaultPayoutProcessorConfigHasEmptyBitcoinRpcAdapterList()
    {
        var config = new PayoutProcessorConfig();

        Assert.NotNull(config.BitcoinRpcAdapters);
        Assert.Empty(config.BitcoinRpcAdapters);
    }

    [Fact]
    public void Validate_NullAdapterListIsAcceptedAsEmpty()
    {
        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(null);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_NullConfigAdapterPropertyIsAcceptedAsEmpty()
    {
        var config = new PayoutProcessorConfig
        {
            BitcoinRpcAdapters = null
        };

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(config.BitcoinRpcAdapters);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_DisabledEntryWithEmptySecretsIsAccepted()
    {
        var result = PayoutProcessorBitcoinRpcAdapterValidator.Validate(new[]
        {
            new PayoutProcessorBitcoinRpcAdapterConfig
            {
                Enabled = false,
                PoolId = string.Empty,
                Coin = string.Empty,
                Endpoint = string.Empty,
                Username = string.Empty,
                Password = string.Empty
            }
        });

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_DisabledEndpointWithUserInfoIsIgnored()
    {
        var result = PayoutProcessorBitcoinRpcAdapterValidator.Validate(new[]
        {
            new PayoutProcessorBitcoinRpcAdapterConfig
            {
                Enabled = false,
                Endpoint = "http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443"
            }
        });

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("PoolId", "bitcoin_rpc_adapter_missing_pool_id")]
    [InlineData("Coin", "bitcoin_rpc_adapter_missing_coin")]
    [InlineData("Endpoint", "bitcoin_rpc_adapter_missing_endpoint")]
    [InlineData("Username", "bitcoin_rpc_adapter_missing_username")]
    [InlineData("Password", "bitcoin_rpc_adapter_missing_password")]
    public void Validate_EnabledEntryRequiresRoutingAndCredentialFields(string fieldName, string expectedCode)
    {
        var config = EnabledConfig();
        SetStringProperty(config, fieldName, " ");

        var result = PayoutProcessorBitcoinRpcAdapterValidator.Validate(new[] { config });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Code == expectedCode);
        AssertDoesNotLeakSecret(result, config.Password);
    }

    [Theory]
    [InlineData("PoolId", " pool-a", "bitcoin_rpc_adapter_poolid_has_outer_whitespace")]
    [InlineData("PoolId", "pool-a ", "bitcoin_rpc_adapter_poolid_has_outer_whitespace")]
    [InlineData("Coin", " bitcoin", "bitcoin_rpc_adapter_coin_has_outer_whitespace")]
    [InlineData("Coin", "bitcoin ", "bitcoin_rpc_adapter_coin_has_outer_whitespace")]
    [InlineData("Username", " rpc-user", "bitcoin_rpc_adapter_username_has_outer_whitespace")]
    [InlineData("Username", "rpc-user ", "bitcoin_rpc_adapter_username_has_outer_whitespace")]
    public void Validate_EnabledEntryRejectsOuterWhitespaceForRouteIdentityFields(
        string fieldName,
        string value,
        string expectedCode)
    {
        var config = EnabledConfig();
        SetStringProperty(config, fieldName, value);

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { config });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Code == expectedCode);
        AssertDoesNotLeakSecret(result, config.Password);
    }

    [Theory]
    [InlineData("http://127.0.0.1:18443")]
    [InlineData("https://127.0.0.1:18443")]
    public void Validate_EnabledEndpointHttpAndHttpsPass(string endpoint)
    {
        var config = EnabledConfig();
        config.Endpoint = endpoint;

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { config });

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("not an absolute uri")]
    public void Validate_EnabledRelativeOrInvalidEndpointFails(string endpoint)
    {
        var config = EnabledConfig();
        config.Endpoint = endpoint;

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { config });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_endpoint_invalid");
        AssertDoesNotLeakSecret(result, endpoint);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:18443")]
    [InlineData("file:///tmp/wallet")]
    [InlineData("custom://127.0.0.1:18443")]
    public void Validate_EnabledUnsupportedEndpointSchemeFails(string endpoint)
    {
        var config = EnabledConfig();
        config.Endpoint = endpoint;

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { config });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_endpoint_scheme_unsupported");
        AssertDoesNotLeakSecret(result, endpoint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_EnabledEntryRequiresPositiveTimeout(int timeoutSeconds)
    {
        var config = EnabledConfig().WithTimeout(timeoutSeconds);

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { config });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_invalid_timeout");
        AssertDoesNotLeakSecret(result, config.Password);
    }

    [Fact]
    public void Validate_EnabledEntryRequiresAtLeastOneAllowedSendMethod()
    {
        var config = EnabledConfig();
        config.AllowSendMany = false;
        config.AllowSendToAddress = false;

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { config });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_no_allowed_methods");
        AssertDoesNotLeakSecret(result, config.Password);
    }

    [Fact]
    public void Validate_DuplicateEnabledPoolAndCoinFails()
    {
        var first = EnabledConfig();
        var second = EnabledConfig();
        second.Endpoint = "http://127.0.0.1:18444";

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { first, second });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_duplicate_pool_coin");
        AssertDoesNotLeakSecret(result, first.Password);
    }

    [Fact]
    public void Validate_DuplicateDisabledPoolAndCoinIsIgnored()
    {
        var first = EnabledConfig();
        first.Enabled = false;
        var second = EnabledConfig();
        second.Enabled = false;

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { first, second });

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_DuplicateEnabledPoolAndCoinComparisonIsOrdinalIgnoreCase()
    {
        var first = EnabledConfig();
        first.PoolId = "pool-a";
        first.Coin = "bitcoin";
        var second = EnabledConfig();
        second.PoolId = "POOL-A";
        second.Coin = "BITCOIN";
        second.Endpoint = "http://127.0.0.1:18444";

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { first, second });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_duplicate_pool_coin");
    }

    [Fact]
    public void Validate_ErrorMessagesNeverIncludePasswordValue()
    {
        const string secret = "SUPER_SECRET_PASSWORD";
        var config = EnabledConfig();
        config.Password = secret;
        config.Endpoint = string.Empty;
        config.RequestTimeoutSeconds = 0;

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { config });

        Assert.False(result.IsValid);
        AssertDoesNotLeakSecret(result, secret);
    }

    [Fact]
    public void Validate_MissingPasswordDoesNotLeakOtherSecretFields()
    {
        const string username = "rpc-user";
        const string endpoint = "http://127.0.0.1:18443";
        const string walletName = "secret-wallet";
        var config = EnabledConfig();
        config.Username = username;
        config.Endpoint = endpoint;
        config.WalletName = walletName;
        config.Password = string.Empty;

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { config });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Code == "bitcoin_rpc_adapter_missing_password");
        AssertDoesNotLeakSecret(result, username);
        AssertDoesNotLeakSecret(result, endpoint);
        AssertDoesNotLeakSecret(result, walletName);
    }

    [Fact]
    public void Validate_EnabledEndpointWithUserInfoFailsWithoutLeakingEndpointOrCredentials()
    {
        const string username = "rpc-user";
        const string password = "SUPER_SECRET_PASSWORD";
        const string endpoint = "http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443";
        var config = EnabledConfig();
        config.Username = username;
        config.Password = password;
        config.Endpoint = endpoint;

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { config });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors,
            x => x.Code == "bitcoin_rpc_adapter_endpoint_contains_credentials");
        AssertDoesNotLeakSecret(result, endpoint);
        AssertDoesNotLeakSecret(result, username);
        AssertDoesNotLeakSecret(result, password);
    }

    [Fact]
    public void Validate_EnabledEndpointWithoutUserInfoPasses()
    {
        var config = EnabledConfig();
        config.Endpoint = "http://127.0.0.1:18443";

        var result = PayoutProcessorBitcoinRpcAdapterValidator.ValidateAdapters(new[] { config });

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ToSafeSummaryDoesNotIncludePasswordUsernameEndpointOrWalletName()
    {
        const string username = "rpc-user";
        const string secret = "SUPER_SECRET_PASSWORD";
        const string endpoint = "http://rpc-user:SUPER_SECRET_PASSWORD@127.0.0.1:18443";
        const string walletName = "secret-wallet";
        var config = EnabledConfig();
        config.Username = username;
        config.Password = secret;
        config.Endpoint = endpoint;
        config.WalletName = walletName;

        var summary = config.ToSafeSummary();

        Assert.DoesNotContain(secret, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(username, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(walletName, summary, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", summary, StringComparison.Ordinal);
        Assert.Contains("UsernameSet=True", summary, StringComparison.Ordinal);
        Assert.Contains("EndpointSet=True", summary, StringComparison.Ordinal);
        Assert.Contains("WalletNameSet=True", summary, StringComparison.Ordinal);
    }

    private static PayoutProcessorBitcoinRpcAdapterConfig EnabledConfig()
    {
        return new PayoutProcessorBitcoinRpcAdapterConfig
        {
            Enabled = true,
            PoolId = "pool-a",
            Coin = "bitcoin",
            Endpoint = "http://127.0.0.1:18443",
            Username = "rpc-user",
            Password = "rpc-password",
            RequestTimeoutSeconds = 30,
            AllowSendMany = true,
            AllowSendToAddress = true
        };
    }

    private static void SetStringProperty(PayoutProcessorBitcoinRpcAdapterConfig config, string fieldName, string value)
    {
        typeof(PayoutProcessorBitcoinRpcAdapterConfig).GetProperty(fieldName).SetValue(config, value);
    }

    private static void AssertDoesNotLeakSecret(
        PayoutProcessorBitcoinRpcAdapterValidationResult result,
        string secret)
    {
        if(string.IsNullOrWhiteSpace(secret))
            return;

        Assert.All(result.Errors, error =>
        {
            Assert.DoesNotContain(secret, error.Code, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, error.PoolId, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, error.Coin, StringComparison.Ordinal);
        });
    }
}

internal static class PayoutProcessorBitcoinRpcAdapterConfigTestExtensions
{
    public static PayoutProcessorBitcoinRpcAdapterConfig WithTimeout(
        this PayoutProcessorBitcoinRpcAdapterConfig config,
        int timeoutSeconds)
    {
        config.RequestTimeoutSeconds = timeoutSeconds;
        return config;
    }
}
