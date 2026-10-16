using Everywhere.StrategyEngine.BuiltIn;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.StrategyEngine;

/// <summary>
/// Extension methods for registering Strategy Engine services.
/// </summary>
public static class StrategyEngineServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Strategy Engine services to the service collection.
    /// </summary>
    public static IServiceCollection AddStrategyEngine(this IServiceCollection services) =>
        services
            // Register core services
            .AddSingleton<IStrategyRegistry, StrategyRegistry>()
            .AddSingleton<IStrategyEngine, StrategyEngine>()

            // Register built-in strategies
            .AddSingleton<IStrategyProvider, GlobalStrategyProvider>()
            .AddSingleton<IStrategyProvider, BrowserStrategyProvider>()
            .AddSingleton<IStrategyProvider, CodeEditorStrategyProvcider>()
            .AddSingleton<IStrategyProvider, TextSelectionStrategyProvider>()
            .AddSingleton<IStrategyProvider, FileStrategyProvider>();
}
