using Everywhere.Chat.Plugins;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Interop;
using Microsoft.Extensions.DependencyInjection;
using Pure.DI;
using static Pure.DI.Lifetime;

namespace Everywhere.DependencyInjection;

public partial class CoreComposition
{
    // Core services sometimes depend on platform or cloud services that are
    // registered by later compositions. This file is the intentional boundary
    // where Core asks the final MS provider for those external roots.
    // ReSharper disable once UnusedMember.Local
    private void SetupExternalBoundaryServices() =>
        DI.Setup()
            // Final MS provider bridge.
            .Bind<IServiceProvider>().To(_ => ServiceProvider)

            // Cloud and platform services consumed by Core.
            .Bind<IChatPluginManager>().As(Singleton).To((IServiceProvider x) => x.GetRequiredService<IChatPluginManager>())
            .Bind<ICloudClient>().As(Singleton).To((IServiceProvider x) => x.GetRequiredService<ICloudClient>())
            .Bind<IOfficialModelProvider>().As(Singleton).To((IServiceProvider x) => x.GetRequiredService<IOfficialModelProvider>())
            .Bind<IVisualElementContext>().As(Singleton).To((IServiceProvider x) => x.GetRequiredService<IVisualElementContext>())
            .Bind<IShortcutListener>().As(Singleton).To((IServiceProvider x) => x.GetRequiredService<IShortcutListener>())
            .Bind<INativeHelper>().As(Singleton).To((IServiceProvider x) => x.GetRequiredService<INativeHelper>())
            .Bind<IWindowHelper>().As(Singleton).To((IServiceProvider x) => x.GetRequiredService<IWindowHelper>())
            .Bind<ISoftwareUpdater>().As(Singleton).To((IServiceProvider x) => x.GetRequiredService<ISoftwareUpdater>());
}