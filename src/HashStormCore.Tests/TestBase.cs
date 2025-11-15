using System;
using Autofac;
using Newtonsoft.Json;

namespace HashStormCore.Tests;

public abstract class TestBase
{
    protected TestBase()
    {
        ModuleInitializer.Initialize();

        container = ModuleInitializer.Container;
        jsonSerializerSettings = container.Resolve<JsonSerializerSettings>();
    }

    protected readonly IContainer container;
    protected readonly JsonSerializerSettings jsonSerializerSettings;

    protected bool IsGithubActionRunner => Environment.GetEnvironmentVariable("GITHUB_ACTION") != null;
}
