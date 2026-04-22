using NBitcoin;

namespace HashStormCore.Blockchain.Equihash.Zcash;

internal static class ZcashNetworkRegistrar
{
    private static readonly object syncRoot = new();
    private static bool isRegistered;

    public static void EnsureRegistered()
    {
        if(isRegistered)
            return;

        lock(syncRoot)
        {
            if(isRegistered)
                return;

            RegisterAlias("zcash-main", Network.Main);
            RegisterAlias("zcash-test", Network.TestNet);
            RegisterAlias("zcash-reg", Network.RegTest);

            isRegistered = true;
        }
    }

    private static void RegisterAlias(string name, Network source)
    {
        if(Network.GetNetwork(name) != null)
            return;

        var builder = new NetworkBuilder();
        builder.CopyFrom(source);
        builder.SetName(name);
        builder.BuildAndRegister();
    }
}
