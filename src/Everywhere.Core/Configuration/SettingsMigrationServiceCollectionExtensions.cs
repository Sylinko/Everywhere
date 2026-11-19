using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Everywhere.Configuration;

/// <summary>
/// Registers settings migrations with static version metadata and ordinary DI activation.
/// </summary>
public static class SettingsMigrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers a migration's version and supplies a transient registration when the concrete type is not already registered.
    /// </summary>
    /// <remarks>
    /// Existing registrations, including custom factories, are preserved. Only selected migrations are resolved;
    /// their instances and container-owned dependencies follow the service provider's normal disposal rules.
    /// </remarks>
    public static IServiceCollection AddSettingsMigration<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services)
        where T : SettingsMigration, ISettingsMigrationMetadata
    {
        services.TryAddTransient<T>();
        services.AddSingleton(new SettingsMigrationRegistration(T.Version, static provider => provider.GetRequiredService<T>()));

        return services;
    }
}