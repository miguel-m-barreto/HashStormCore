using System.IO;
using System.Reflection;
using HashStormCore.Configuration;
using NLog;
using NLog.Layouts;
using Xunit;

namespace HashStormCore.Tests;

public class ProgramCompatibilityTests
{
    [Fact]
    public void GetLogPath_ReturnsFileName_WhenBaseDirectoryIsMissing()
    {
        var result = RenderLogPath(new ClusterLoggingConfig(), "hashstorm.log");

        Assert.Equal("hashstorm.log", result);
    }

    [Fact]
    public void GetLogPath_CombinesBaseDirectoryAndFileName()
    {
        var result = RenderLogPath(new ClusterLoggingConfig
        {
            LogBaseDirectory = "/var/log/hashstorm"
        }, "api.log");

        Assert.Equal(Path.Combine("/var/log/hashstorm", "api.log"), result);
    }

    private static string RenderLogPath(ClusterLoggingConfig config, string name)
    {
        var method = typeof(HashStormCore.Program).GetMethod("GetLogPath", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var layout = (Layout) method.Invoke(null, new object[] { config, name });

        return layout.Render(LogEventInfo.CreateNullEvent());
    }
}
