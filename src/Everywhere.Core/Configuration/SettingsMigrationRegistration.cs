using Everywhere.Common;

namespace Everywhere.Configuration;

/// <summary>
/// Holds version metadata and defers migration resolution to the container that owns the registration.
/// </summary>
public sealed record SettingsMigrationRegistration(SemanticVersion Version, Func<IServiceProvider, SettingsMigration> Resolve);