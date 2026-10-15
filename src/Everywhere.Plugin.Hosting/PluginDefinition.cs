using Everywhere.Plugin.Hosting.Installation;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Hosting;

/// <summary>
/// Represents one selected platform implementation before assembly graph validation.
/// </summary>
public sealed class PluginDefinition
{
    /// <summary>
    /// Gets the complete installed package.
    /// </summary>
    public PluginInstallation Installation { get; }

    /// <summary>
    /// Gets the concrete entry and its platform-specific dependency declarations.
    /// </summary>
    public UniversalPluginEntry Entry { get; }

    /// <summary>
    /// Gets the stable plugin identifier.
    /// </summary>
    public string Id => Installation.Manifest.Id;

    /// <summary>
    /// Gets the writable data location independent of this installation's lifetime.
    /// </summary>
    public string DataDirectory { get; }

    /// <summary>
    /// Selects a concrete entry before testing compatibility. An incompatible specific platform entry
    /// never falls back to another implementation merely because its version range would match.
    /// </summary>
    public PluginDefinition(
        PluginInstallation installation,
        string dataDirectory,
        string runtimeIdentifier,
        string hostVersion,
        string pluginApiVersion,
        string hostFramework)
    {
        Installation = installation;
        DataDirectory = Path.GetFullPath(dataDirectory);
        Entry = PluginPlatformSelector.Select(installation.Manifest, runtimeIdentifier);

        if (!PluginCompatibility.IsSatisfied(Entry.PluginApiVersion, pluginApiVersion) ||
            Entry.HostVersion is { } range && !PluginCompatibility.IsSatisfied(range, hostVersion) ||
            !PluginCompatibility.IsFrameworkCompatible(hostFramework, Entry.Framework))
        {
            throw new PluginValidationException("IncompatibleHost", $"Plugin '{Id}' is incompatible with this host.");
        }
    }
}