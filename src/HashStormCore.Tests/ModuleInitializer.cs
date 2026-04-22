using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autofac;
using HashStormCore.Configuration;
using HashStormCore.Mappings;
using HashStormCore.Native;
using HashStormCore.Tests.Util;
using HashStormCore.Time;

namespace HashStormCore.Tests;

public static class ModuleInitializer
{
    private static readonly object initLock = new object();

    private static bool isInitialized = false;
    private static IContainer container;
    private static Dictionary<string, CoinTemplate> coinTemplates;

    public static IContainer Container => container;
    public static Dictionary<string, CoinTemplate> CoinTemplates => coinTemplates;

    /// <summary>
    /// Initializes the module.
    /// </summary>
    public static void Initialize()
    {
        lock(initLock)
        {
            if(isInitialized)
                return;

            var builder = new ContainerBuilder();

            builder.RegisterAssemblyModules(typeof(AutofacModule).GetTypeInfo().Assembly);

            builder.RegisterType<ObjectMapper>().As<IObjectMapper>().SingleInstance();

            builder.RegisterType<MockMasterClock>().AsImplementedInterfaces();

            // Autofac Container
            container = builder.Build();

            isInitialized = true;

            // Load coin templates
            var basePath = Path.GetDirectoryName(typeof(Program).Assembly.Location);
            var defaultDefinitions = Path.Combine(basePath, "coins.json");

            var coinDefs = new[]
            {
                defaultDefinitions
            };

            coinTemplates = CoinTemplateLoader.Load(container, coinDefs);

            Cryptonight.InitContexts(1);
        }
    }
}
