using Everywhere.StrategyEngine;
using Everywhere.StrategyEngine.BuiltIn;
using Pure.DI;
using static Pure.DI.Lifetime;

namespace Everywhere.DependencyInjection;

public partial class CoreComposition
{
    // ReSharper disable once UnusedMember.Local
    private static void SetupStrategyEngineServices() =>
        DI.Setup()
            // Strategy engine core services.
            .Bind<IStrategyRegistry>().As(Singleton).To<StrategyRegistry>()
            .Bind<IStrategyEngine>().As(Singleton).To<Everywhere.StrategyEngine.StrategyEngine>()

            // Built-in strategy providers.
            .Bind<IStrategyProvider>(Tag.Unique).As(Singleton).To<GlobalStrategyProvider>()
            .Bind<IStrategyProvider>(Tag.Unique).As(Singleton).To<BrowserStrategyProvider>()
            .Bind<IStrategyProvider>(Tag.Unique).As(Singleton).To<CodeEditorStrategyProvcider>()
            .Bind<IStrategyProvider>(Tag.Unique).As(Singleton).To<TextSelectionStrategyProvider>()
            .Bind<IStrategyProvider>(Tag.Unique).As(Singleton).To<FileStrategyProvider>();
}
