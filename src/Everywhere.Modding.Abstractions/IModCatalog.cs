using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Modding.Abstractions;

/// <summary>
/// Observes mods without loading code or retaining mod-owned objects.
/// </summary>
public interface IModCatalog
{
    /// <summary>
    /// Returns whether a declared dependency is compatible, selected and initialized for this activation.
    /// An undeclared identifier is an API error. This check does not extend another mod's lifetime.
    /// </summary>
    bool IsAvailable(string modId);

    /// <summary>
    /// Gets a snapshot for any mod known to the host without changing dependency relationships.
    /// Returns false for an unknown identifier.
    /// </summary>
    bool TryGetInfo(string modId, [NotNullWhen(true)] out ModInfo? info);
}