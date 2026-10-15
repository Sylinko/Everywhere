using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Plugin.Abstractions;

/// <summary>
/// Observes plugins without loading code or retaining plugin-owned objects.
/// </summary>
public interface IPluginCatalog
{
    /// <summary>
    /// Returns whether a declared dependency is compatible, selected and initialized for this activation.
    /// An undeclared identifier is an API error. This check does not extend another plugin's lifetime.
    /// </summary>
    bool IsAvailable(string pluginId);

    /// <summary>
    /// Gets a snapshot for any plugin known to the host without changing dependency relationships.
    /// Returns false for an unknown identifier.
    /// </summary>
    bool TryGetInfo(string pluginId, [NotNullWhen(true)] out PluginInfo? info);
}