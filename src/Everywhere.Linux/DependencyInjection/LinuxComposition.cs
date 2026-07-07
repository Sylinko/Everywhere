using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Linux.Chat.Plugins;
using Everywhere.Linux.Common;
using Everywhere.Linux.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pure.DI;
using Pure.DI.MS;
using static Pure.DI.Lifetime;

namespace Everywhere.Linux.DependencyInjection;

public partial class LinuxComposition : ServiceProviderFactory<LinuxComposition>
{
    private const RootKinds ExportedRoot = RootKinds.Default | RootKinds.Exported;

    // Platform compositions only bind native services, the platform plugin, and
    // startup initializers. Shared app services live in CoreComposition.
    // ReSharper disable once UnusedMember.Local
    private void SetupLinuxServices() =>
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

            // Linux X11 interop services.
            .Bind<X11WindowBackend>().Bind<IEventHelper>().Bind<IWindowBackend>().Bind<IWindowHelper>().As(Singleton).To<X11WindowBackend>()
            .Bind<IVisualElementContext>().As(Singleton).To<VisualElementContext>()
            .Bind<IShortcutListener>().As(Singleton).To<ShortcutListener>()
            .Bind<INativeHelper>().As(Singleton).To<NativeHelper>()

            // Linux update services.
            .Bind<IPlatformUpdateHandler>().As(Singleton).To<LinuxUpdateHandler>()
            .Bind<ISoftwareUpdater>().As(Singleton).To<SoftwareUpdater>()

            // Linux chat plugin and plugin manager.
            .Bind<FdFindPlugin>().Bind<BuiltInChatPlugin>(Tag.Unique).As(Singleton).To<FdFindPlugin>()
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

            // Linux roots exported to the final MS provider.
            .Root<X11WindowBackend>(kind: ExportedRoot)
            .Root<IEventHelper>(kind: ExportedRoot)
            .Root<IWindowBackend>(kind: ExportedRoot)
            .Root<IWindowHelper>(kind: ExportedRoot)
            .Root<IVisualElementContext>(kind: ExportedRoot)
            .Root<IShortcutListener>(kind: ExportedRoot)
            .Root<INativeHelper>(kind: ExportedRoot)
            .Root<IPlatformUpdateHandler>(kind: ExportedRoot)
            .Root<ISoftwareUpdater>(kind: ExportedRoot)
            .Root<FdFindPlugin>(kind: ExportedRoot)
            .Root<ChatWindowInitializer>(kind: ExportedRoot)
            .Root<UpdaterInitializer>(kind: ExportedRoot)
            .Root<ChatPluginManager>(kind: ExportedRoot)
            .Root<IChatPluginManager>(kind: ExportedRoot);
}