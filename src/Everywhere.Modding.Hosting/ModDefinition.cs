using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting;

/// <summary>
/// Represents one selected platform implementation before assembly graph validation.
/// </summary>
public sealed class ModDefinition
{
    /// <summary>
    /// Gets the complete installed package.
    /// </summary>
    public ModInstallation Installation { get; }

    /// <summary>
    /// Gets the concrete entry and its dependencies. Access only when SkipReason is null.
    /// </summary>
    public UniversalModEntry Entry => _entry ?? throw new InvalidOperationException("This mod has no entry for the current platform.");

    /// <summary>
    /// Gets why this enabled declaration cannot be activated on the current host.
    /// </summary>
    public ModSkipReason? SkipReason { get; }

    /// <summary>
    /// Gets a host compatibility failure without discarding this requested mod's identity.
    /// </summary>
    public ModDiagnostic? Diagnostic { get; }

    /// <summary>
    /// Gets the stable mod identifier.
    /// </summary>
    public string Id => Installation.Manifest.Id;

    /// <summary>
    /// Gets the writable data location independent of this installation's lifetime.
    /// </summary>
    public string DataDirectory { get; }

    private readonly UniversalModEntry? _entry;

    /// <summary>
    /// Selects a concrete entry before testing compatibility. An incompatible specific platform entry
    /// never falls back to another implementation merely because its version range would match.
    /// </summary>
    public ModDefinition(
        ModInstallation installation,
        string dataDirectory,
        string runtimeIdentifier,
        string hostVersion,
        string modApiVersion,
        string hostFramework)
    {
        Installation = installation;
        DataDirectory = Path.GetFullPath(dataDirectory);
        _entry = ModPlatformSelector.Select(installation.Manifest, runtimeIdentifier);

        if (_entry is null)
        {
            SkipReason = ModSkipReason.UnsupportedPlatform;
            return;
        }

        if (!ModCompatibility.IsSatisfied(Entry.ModApiVersion, modApiVersion) ||
            Entry.HostVersion is { } range && !ModCompatibility.IsSatisfied(range, hostVersion) ||
            !ModCompatibility.IsFrameworkCompatible(hostFramework, Entry.Framework))
        {
            Diagnostic = new ModDiagnostic
            {
                ModId = Id,
                Code = "IncompatibleHost",
                Detail = $"Mod '{Id}' is incompatible with this host."
            };
        }
    }
}