using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Windows.Chat.Plugins;
using Everywhere.Windows.Common;
using Everywhere.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pure.DI;
using Pure.DI.MS;
using static Pure.DI.Lifetime;

namespace Everywhere.Windows.DependencyInjection;

public partial class WindowsComposition : ServiceProviderFactory<WindowsComposition>
{
    private const RootKinds ExportedRoot = RootKinds.Default | RootKinds.Exported;

    // Platform compositions only bind native services, the platform plugin, and
    // startup initializers. Shared app services live in CoreComposition.
    // ReSharper disable once UnusedMember.Local
    private void SetupWindowsServices() =>
        DI.Setup()
            // Pure.DI.MS hooks and framework fallbacks.
            .Hint(Hint.OnCannotResolve, "On")
            .Hint(Hint.OnCannotResolvePartial, "Off")
            .Hint(Hint.OnNewRoot, "On")
            .Hint(Hint.OnNewRootPartial, "Off")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "Microsoft.Extensions.*")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "Microsoft.EntityFrameworkCore.*")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "System.Net.Http.*")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "Everywhere.*")

            // Final MS provider and logging bridge.
            .Bind<IServiceProvider>().To(_ => ServiceProvider)
            .Bind<ILogger<TT>>().As(Singleton).To<Logger<TT>>()

            // Platform-wide notification publishers.
            .Bind<INotificationPublisher<TT>>().As(Singleton).To<NotificationPublisher<TT>>()

            // Windows native interop services.
            .Bind<IVisualElementContext>().As(Singleton).To<VisualElementContext>()
            .Bind<IShortcutListener>().As(Singleton).To<ShortcutListener>()
            .Bind<INativeHelper>().As(Singleton).To<NativeHelper>()
            .Bind<IWindowHelper>().As(Singleton).To<WindowHelper>()

            // Windows update services.
            .Bind<IPlatformUpdateHandler>().As(Singleton).To<WindowsUpdateHandler>()
            .Bind<ISoftwareUpdater>().As(Singleton).To<SoftwareUpdater>()

            // Windows chat plugin and plugin manager.
            .Bind<EverythingPlugin>().Bind<BuiltInChatPlugin>(Tag.Unique).As(Singleton).To<EverythingPlugin>()
            .Bind<ChatPluginManager>().Bind<IChatPluginManager>().As(Singleton).To((
                    IServiceProvider serviceProvider,
                    Settings settings,
                    IRuntimeManager runtimeManager,
                    ILogger<ChatPluginManager> logger) =>
                new ChatPluginManager(
                    serviceProvider,
                    serviceProvider.GetRequiredService<IEnumerable<BuiltInChatPlugin>>(),
                    settings,
                    runtimeManager,
                    logger))

            // Platform startup initializers.
            .Bind<ChatWindowInitializer>().To<ChatWindowInitializer>()
            .Bind<UpdaterInitializer>().To<UpdaterInitializer>()

            // Windows roots exported to the final MS provider.
            .Root<IVisualElementContext>(kind: ExportedRoot)
            .Root<IShortcutListener>(kind: ExportedRoot)
            .Root<INativeHelper>(kind: ExportedRoot)
            .Root<IWindowHelper>(kind: ExportedRoot)
            .Root<IPlatformUpdateHandler>(kind: ExportedRoot)
            .Root<ISoftwareUpdater>(kind: ExportedRoot)
            .Root<EverythingPlugin>(kind: ExportedRoot)
            .Root<ChatWindowInitializer>(kind: ExportedRoot)
            .Root<UpdaterInitializer>(kind: ExportedRoot)
            .Root<ChatPluginManager>(kind: ExportedRoot)
            .Root<IChatPluginManager>(kind: ExportedRoot);
}