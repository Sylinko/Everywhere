using Everywhere.Interop;
using Pure.DI;
using static Pure.DI.Lifetime;

namespace Everywhere.DependencyInjection;

public partial class CoreComposition
{
    // ReSharper disable once UnusedMember.Local
    private static void SetupInteropServices() =>
        DI.Setup()
            // Core watchdog bridge.
            .Bind<WatchdogManager>().Bind<IWatchdogManager>().As(Singleton).To<WatchdogManager>();
}