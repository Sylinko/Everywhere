using System.Diagnostics.CodeAnalysis;
using Everywhere.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Everywhere.Initialization;

/// <summary>
/// Registers ordered startup callbacks while preserving DI construction and ownership.
/// </summary>
public static class AsyncInitializerServiceCollectionExtensions
{
    /// <summary>
    /// Registers static ordering metadata and a deferred invocation. An existing concrete
    /// service registration is preserved; otherwise a transient registration is supplied.
    /// </summary>
    public static IServiceCollection AddAsyncInitializer<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services)
        where T : class, IAsyncInitializer
    {
        services.TryAddTransient<T>();
        services.AddSingleton(
            new AsyncInitializerRegistration(
                T.Index,
                static (provider, cancellationToken) => provider.GetRequiredService<T>().InitializeAsync(cancellationToken)));

        return services;
    }
}