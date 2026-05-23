using System;
using System.IO;
using HashStormCore.Payouts.CoinMetadata;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class CoinsJsonCoinMetadataRegistryTests
{
    [Fact]
    public void TryGetCoinLooksUpCoinKeyCaseInsensitivelyAndParsesMetadata()
    {
        using var temp = new TempCoinsJson(@"{
  ""testcoin"": {
    ""name"": ""Test Coin"",
    ""canonicalName"": ""testcoin"",
    ""symbol"": ""TST"",
    ""family"": ""bitcoin"",
    ""hasBrokenSendMany"": true,
    ""useBitcoinPayoutHandler"": true,
    ""usesZCashAddressFormat"": true,
    ""payoutDecimalPlaces"": 8
  }
}");
        var registry = new CoinsJsonCoinMetadataRegistry(temp.Path);

        var found = registry.TryGetCoin("TESTCOIN", out var descriptor);

        Assert.True(found);
        Assert.Equal("testcoin", descriptor.CoinKey);
        Assert.Equal("Test Coin", descriptor.Name);
        Assert.Equal("testcoin", descriptor.CanonicalName);
        Assert.Equal("TST", descriptor.Symbol);
        Assert.Equal("bitcoin", descriptor.Family);
        Assert.True(descriptor.HasBrokenSendMany);
        Assert.True(descriptor.UseBitcoinPayoutHandler);
        Assert.True(descriptor.UsesZCashAddressFormat);
        Assert.True(descriptor.HasUsesZCashAddressFormat);
        Assert.Equal(8, descriptor.PayoutDecimalPlaces);
    }

    [Fact]
    public void MissingUsesZCashAddressFormatDoesNotDefaultTrueForEquihash()
    {
        using var temp = new TempCoinsJson(@"{
  ""zec"": {
    ""name"": ""Zcash"",
    ""canonicalName"": ""zcash"",
    ""symbol"": ""ZEC"",
    ""family"": ""equihash""
  }
}");
        var registry = new CoinsJsonCoinMetadataRegistry(temp.Path);

        var found = registry.TryGetCoin("zec", out var descriptor);

        Assert.True(found);
        Assert.Equal("equihash", descriptor.Family);
        Assert.False(descriptor.UsesZCashAddressFormat);
        Assert.False(descriptor.HasUsesZCashAddressFormat);
    }

    [Fact]
    public void ExplicitFalseUsesZCashAddressFormatIsPreserved()
    {
        using var temp = new TempCoinsJson(@"{
  ""equihashfork"": {
    ""name"": ""Equihash Fork"",
    ""canonicalName"": ""equihashfork"",
    ""symbol"": ""EQUI"",
    ""family"": ""equihash"",
    ""usesZCashAddressFormat"": false
  }
}");
        var registry = new CoinsJsonCoinMetadataRegistry(temp.Path);

        var found = registry.TryGetCoin("equihashfork", out var descriptor);

        Assert.True(found);
        Assert.False(descriptor.UsesZCashAddressFormat);
        Assert.True(descriptor.HasUsesZCashAddressFormat);
    }

    [Fact]
    public void UnknownCoinReturnsFalse()
    {
        using var temp = new TempCoinsJson(@"{ ""bitcoin"": { ""symbol"": ""BTC"", ""family"": ""bitcoin"" } }");
        var registry = new CoinsJsonCoinMetadataRegistry(temp.Path);

        var found = registry.TryGetCoin("missing", out _);

        Assert.False(found);
    }

    [Fact]
    public void MissingCoinsJsonPathThrowsFileNotFoundException()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "missing-coins-" + Guid.NewGuid().ToString("N") + ".json");
        var registry = new CoinsJsonCoinMetadataRegistry(path);

        var ex = Assert.Throws<FileNotFoundException>(() => registry.TryGetCoin("bitcoin", out _));

        Assert.Contains("coins.json metadata file was not found", ex.Message);
        Assert.Equal(path, ex.FileName);
    }

    private sealed class TempCoinsJson : IDisposable
    {
        public TempCoinsJson(string json)
        {
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hashstorm-coins-json-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "coins.json");
            File.WriteAllText(Path, json);
        }

        private readonly string directory;

        public string Path { get; }

        public void Dispose()
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
