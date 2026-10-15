namespace Everywhere.Plugin.Abstractions;

/// <summary>
/// Describes a plugin using host-owned values only; a snapshot does not retain the activation.
/// </summary>
public sealed record PluginInfo
{
    /// <summary>
    /// Gets the stable plugin identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the plugin's semantic version, independent of its assembly version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets whether the desired configuration requests this plugin to be enabled.
    /// </summary>
    public required bool IsEnabled { get; init; }

    /// <summary>
    /// Gets lifecycle progress, not physical assembly collection status.
    /// </summary>
    public required PluginState State { get; init; }
}