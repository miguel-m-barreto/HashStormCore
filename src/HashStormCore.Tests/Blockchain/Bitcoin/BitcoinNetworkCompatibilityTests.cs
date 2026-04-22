using System.Reflection;
using HashStormCore.Blockchain.Bitcoin;
using HashStormCore.Blockchain.Equihash.Zcash;
using NBitcoin;
using Xunit;

namespace HashStormCore.Tests.Blockchain.Bitcoin;

public class BitcoinNetworkCompatibilityTests
{
    [Fact]
    public void ResolveNetwork_MapsKnownAlternativeFamilyNames()
    {
        Assert.Same(Network.Main, ResolveNetwork("nexa"));
        Assert.Same(Network.TestNet, ResolveNetwork("nexatest"));
        Assert.Same(Network.RegTest, ResolveNetwork("nexareg"));

        Assert.Same(Network.Main, ResolveNetwork("scash"));
        Assert.Same(Network.TestNet, ResolveNetwork("scashtestnet"));
        Assert.Same(Network.RegTest, ResolveNetwork("scashregtest"));
    }

    [Fact]
    public void ResolveNetwork_UsesRegisteredZcashAliases()
    {
        ZcashNetworkRegistrar.EnsureRegistered();

        var main = ResolveNetwork("zcash-main");
        var test = ResolveNetwork("zcash-test");
        var reg = ResolveNetwork("zcash-reg");

        Assert.NotNull(main);
        Assert.Equal("zcash-main", main.Name);
        Assert.Equal(ChainName.Mainnet, main.ChainName);

        Assert.NotNull(test);
        Assert.Equal("zcash-test", test.Name);
        Assert.Equal(ChainName.Testnet, test.ChainName);

        Assert.NotNull(reg);
        Assert.Equal("zcash-reg", reg.Name);
        Assert.Equal(ChainName.Regtest, reg.ChainName);
    }

    private static Network ResolveNetwork(string chainName)
    {
        var method = typeof(BitcoinJobManagerBase<HashStormCore.Blockchain.Bitcoin.BitcoinJob>)
            .GetMethod("ResolveNetwork", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return (Network) method.Invoke(null, new object[] { chainName });
    }
}
