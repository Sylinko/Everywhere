using Everywhere.Common;

namespace Everywhere.Configuration;

/// <summary>
/// Supplies a migration's target version without constructing the migration or resolving its dependencies.
/// </summary>
public interface ISettingsMigrationMetadata
{
    /// <summary>
    /// Gets the settings version reached after this migration succeeds.
    /// </summary>
    static abstract SemanticVersion Version { get; }
}