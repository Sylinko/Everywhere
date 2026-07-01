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
    public static IServiceCollection AddStrategyEngine(this IServiceCollection services)
    {
        // Register core services
        services.AddSingleton<IStrategyRegistry, StrategyRegistry>();
        services.AddSingleton<IStrategyEngine, StrategyEngine>();

        // Register built-in strategies
        services.AddSingleton<IStrategyProvider, GlobalStrategyProvider>();
        services.AddSingleton<IStrategyProvider, BrowserStrategyProvider>();
        services.AddSingleton<IStrategyProvider, CodeEditorStrategyProvcider>();
        services.AddSingleton<IStrategyProvider, TextSelectionStrategyProvider>();
        services.AddSingleton<IStrategyProvider, FileStrategyProvider>();

        return services;
    }
}
