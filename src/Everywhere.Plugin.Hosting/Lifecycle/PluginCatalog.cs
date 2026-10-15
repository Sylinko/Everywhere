using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Everywhere.Plugin.Abstractions;

namespace Everywhere.Plugin.Hosting.Lifecycle;

/// <summary>
/// Exposes host-owned snapshots and stable selected bindings to one activation.
/// </summary>
internal sealed class PluginCatalog(
    IEnumerable<string> declared,
    IEnumerable<string> selected,
    ConcurrentDictionary<string, PluginInfo> snapshots
) : IPluginCatalog
{
    private readonly HashSet<string> _declared = new(declared, StringComparer.Ordinal);
    private readonly HashSet<string> _selected = new(selected, StringComparer.Ordinal);

    /// <inheritdoc />
    public bool IsAvailable(string pluginId)
    {
        if (!_declared.Contains(pluginId))
        {
            throw new InvalidOperationException($"Plugin '{pluginId}' is not a declared dependency.");
        }

        return _selected.Contains(pluginId) && snapshots.TryGetValue(pluginId, out var info) && info.State == PluginState.Active;
    }

    /// <inheritdoc />
    public bool TryGetInfo(string pluginId, [NotNullWhen(true)] out PluginInfo? info)
    {
        return snapshots.TryGetValue(pluginId, out info);
    }
}