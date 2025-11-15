using System.Net;

namespace HashStormCore.Banning;

public interface IBanManager
{
    bool IsBanned(IPAddress address);
    void Ban(IPAddress address, TimeSpan duration);
}
