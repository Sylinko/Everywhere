namespace Everywhere.Plugin.Abstractions;

/// <summary>
/// Explains why an enabled plugin has no activation without treating it as a failed plugin.
/// </summary>
public enum PluginSkipReason
{
    /// <summary>
    /// The package has no entry matching the current runtime identifier.
    /// </summary>
    UnsupportedPlatform
}