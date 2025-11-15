using HashStormCore.Configuration;
using NLog;

namespace HashStormCore.Util;

public static class LogUtil
{
    public static ILogger GetPoolScopedLogger(Type type, PoolConfig poolConfig)
    {
        return LogManager.GetLogger(poolConfig.Id);
    }

    public static ILogger GetPoolScopedLogger(Type type, string poolId)
    {
        return LogManager.GetLogger(poolId);
    }

    public static string DotTerminate(string msg)
    {
        if(!msg.EndsWith("."))
            msg += ".";

        return msg;
    }
}
