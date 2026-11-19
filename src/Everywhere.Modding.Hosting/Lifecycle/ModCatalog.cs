using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Everywhere.Modding.Abstractions;

namespace Everywhere.Modding.Hosting.Lifecycle;

/// <summary>
/// Exposes host-owned snapshots and stable selected bindings to one activation.
/// </summary>
internal sealed class ModCatalog(
    IEnumerable<string> declared,
    IEnumerable<string> selected,
    ConcurrentDictionary<string, ModInfo> snapshots
) : IModCatalog
{
    private readonly HashSet<string> _declared = new(declared, StringComparer.Ordinal);
    private readonly HashSet<string> _selected = new(selected, StringComparer.Ordinal);

    /// <inheritdoc />
    public bool IsAvailable(string modId)
    {
        if (!_declared.Contains(modId))
        {
            throw new InvalidOperationException($"Mod '{modId}' is not a declared dependency.");
        }

        return _selected.Contains(modId) && snapshots.TryGetValue(modId, out var info) && info.State == ModState.Active;
    }

    /// <inheritdoc />
    public bool TryGetInfo(string modId, [NotNullWhen(true)] out ModInfo? info)
    {
        return snapshots.TryGetValue(modId, out info);
    }
}