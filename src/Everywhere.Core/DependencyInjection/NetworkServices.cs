using System.Net;
using Everywhere.Common;
using Everywhere.Initialization;
using Pure.DI;
using static Pure.DI.Lifetime;

namespace Everywhere.DependencyInjection;

public partial class CoreComposition
{
    // ReSharper disable once UnusedMember.Local
    private static void SetupNetworkServices() =>
        DI.Setup()
            // Network facade services.
            .Bind<DynamicWebProxy>().Bind<IWebProxy>().As(Singleton).To<DynamicWebProxy>()
            .Bind<FileDownloadService>().Bind<IFileDownloadService>().As(Singleton).To<FileDownloadService>()
            .Bind<RuntimeManager>().Bind<IRuntimeManager>().As(Singleton).To<RuntimeManager>()

            // Startup network initialization.
            .Bind<NetworkInitializer>().To<NetworkInitializer>();
}