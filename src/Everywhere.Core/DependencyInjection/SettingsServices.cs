using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.Initialization;
using Everywhere.Views;
using Pure.DI;
using static Pure.DI.Lifetime;

namespace Everywhere.DependencyInjection;

// Pure.DI reports platform-specific bindings at the composition setup site.
#pragma warning disable CA1416

public partial class CoreComposition
{
    // ReSharper disable once UnusedMember.Local
    private static void SetupSettingsServices() =>
        DI.Setup()
            // Settings model and engine.
            .Bind<Settings>().As(Singleton).To<Settings>()
            .Bind<SettingsEngine>().To<SettingsEngine>()

            // Settings page controls.
            .Bind<SoftwareUpdateControl>().To<SoftwareUpdateControl>()
#if WINDOWS
            .Bind<RestartAsAdministratorControl>().To<RestartAsAdministratorControl>()
#endif
            .Bind<OpenWebBrowserControl>().To<OpenWebBrowserControl>()
            .Bind<DebugFeaturesControl>().To<DebugFeaturesControl>()

            // Persistent settings storage.
            .Bind<PersistentKeyValueStorage>().Bind<IKeyValueStorage>().As(Singleton).To<PersistentKeyValueStorage>()
            .Bind<PersistentState>().As(Singleton).To<PersistentState>()

            // Settings-backed assistant initialization.
            .Bind<CustomAssistantInitializer>().To<CustomAssistantInitializer>();
}