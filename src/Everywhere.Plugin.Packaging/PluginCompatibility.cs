using NuGet.Frameworks;
using NuGet.Versioning;

namespace Everywhere.Plugin.Packaging;

/// <summary>
/// Shares runtime compatibility semantics between SDK checks and activation preflight.
/// </summary>
public static class PluginCompatibility
{
    /// <summary>
    /// Parses an exact semantic version at the package boundary.
    /// </summary>
    public static NuGetVersion ParseVersion(string value)
    {
        if (!NuGetVersion.TryParse(value, out var version))
        {
            throw new PluginValidationException("InvalidVersion", $"Invalid plugin version '{value}'.");
        }

        return version;
    }

    /// <summary>
    /// Parses a non-floating range. Missing ranges are not interpreted as unlimited compatibility.
    /// </summary>
    public static VersionRange ParseRange(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !VersionRange.TryParse(value, out var range) || range.IsFloating)
        {
            throw new PluginValidationException("InvalidVersionRange", $"Invalid plugin version range '{value}'.");
        }

        return range;
    }

    /// <summary>
    /// Checks a runtime version, admitting prereleases only when a range bound explicitly names one.
    /// </summary>
    public static bool IsSatisfied(string rangeText, string versionText)
    {
        var range = ParseRange(rangeText);
        var version = ParseVersion(versionText);
        var allowsPrerelease = range.MinVersion?.IsPrerelease == true || range.MaxVersion?.IsPrerelease == true;

        return (!version.IsPrerelease || allowsPrerelease) && range.Satisfies(version);
    }

    /// <summary>
    /// Checks that a host target framework can consume the entry's compiled framework.
    /// </summary>
    public static bool IsFrameworkCompatible(string hostFramework, string entryFramework)
    {
        var host = NuGetFramework.ParseFolder(hostFramework);
        var entry = NuGetFramework.ParseFolder(entryFramework);

        return !host.IsUnsupported &&
            !entry.IsUnsupported &&
            DefaultCompatibilityProvider.Instance.IsCompatible(host, entry);
    }
}