using HashStormCore.Blockchain.Bitcoin;
using NBitcoin;
using Xunit;

namespace HashStormCore.Tests.Blockchain.Bitcoin;

public class BitcoinUtilsTests
{
    [Fact]
    public void AddressToDestination_ParsesBitcoinLegacyAddress()
    {
        const string address = "15fNGm51ikaxe9nyMoUjX7Vfo9x8kWRQpY";

        var destination = BitcoinUtils.AddressToDestination(address, Network.Main);

        AssertRoundtrip(destination, Network.Main,
            "76a91433221100ffeeddccbbaa9988776655443322110088ac",
            address);
    }

    [Fact]
    public void BechSegwitAddressToDestination_ParsesBitcoinBech32Address()
    {
        const string address = "bc1qxv3pzq8lamwuewa2nxy8wej4gsejyygqhy0vhq";

        var destination = BitcoinUtils.BechSegwitAddressToDestination(address, Network.Main);

        AssertRoundtrip(destination, Network.Main,
            "001433221100ffeeddccbbaa99887766554433221100",
            address);
    }

    [Fact]
    public void BCashAddressToDestination_ParsesCashAddr()
    {
        const string address = "bitcoincash:qqejyygqllhdmn9m42vcsamx24zrxgs3qqsrl6pyqv";
        var network = NBitcoin.Altcoins.BCash.Instance.GetNetwork(ChainName.Mainnet);

        var destination = BitcoinUtils.BCashAddressToDestination(address, Network.Main);

        AssertRoundtrip(destination, network,
            "76a91433221100ffeeddccbbaa9988776655443322110088ac",
            address);
    }

    [Fact]
    public void LitecoinAddressToDestination_ParsesBase58Address()
    {
        const string address = "LPtKXyNqoQq1txV8XwU2o8ZS1NKQmtv66z";
        var network = NBitcoin.Altcoins.Litecoin.Instance.GetNetwork(ChainName.Mainnet);

        var destination = BitcoinUtils.LitecoinAddressToDestination(address, Network.Main);

        AssertRoundtrip(destination, network,
            "76a91433221100ffeeddccbbaa9988776655443322110088ac",
            address);
    }

    [Fact]
    public void LitecoinAddressToDestination_ParsesBech32Address()
    {
        const string address = "ltc1qxv3pzq8lamwuewa2nxy8wej4gsejyygqnc4g0s";
        var network = NBitcoin.Altcoins.Litecoin.Instance.GetNetwork(ChainName.Mainnet);

        var destination = BitcoinUtils.LitecoinAddressToDestination(address, Network.Main);

        AssertRoundtrip(destination, network,
            "001433221100ffeeddccbbaa99887766554433221100",
            address);
    }

    private static void AssertRoundtrip(IDestination destination, Network network, string expectedScriptHex, string expectedAddress)
    {
        Assert.Equal(expectedScriptHex, destination.ScriptPubKey.ToHex());

        var formatted = destination.ScriptPubKey.GetDestinationAddress(network);

        Assert.NotNull(formatted);
        Assert.Equal(expectedAddress, formatted.ToString());
    }
}
