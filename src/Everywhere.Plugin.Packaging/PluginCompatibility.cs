using NuGet.Frameworks;
using Everywhere.Common;

namespace Everywhere.Plugin.Packaging;

/// <summary>
/// Shares runtime compatibility semantics between SDK checks and activation preflight.
/// </summary>
public static class PluginCompatibility
{
    /// <summary>
    /// Parses an exact semantic version at the package boundary.
    /// </summary>
    public static SemanticVersion ParseVersion(string value)
    {
        if (!SemanticVersion.TryParse(value, out var version))
        {
            throw new PluginValidationException("InvalidVersion", $"Invalid plugin version '{value}'.");
        }

        return version;
    }

    /// <summary>
    /// Parses a non-floating range. Missing ranges are not interpreted as unlimited compatibility.
    /// </summary>
    public static SemanticVersionRange ParseRange(string value)
    {
        if (!SemanticVersionRange.TryParse(value, out var range))
        {
            throw new PluginValidationException("InvalidVersionRange", $"Invalid plugin version range '{value}'.");
        }

        return range;
    }

    /// <summary>
    /// Checks a runtime version using the same range semantics as SDK validation and host versions.
    /// </summary>
    public static bool IsSatisfied(string rangeText, string versionText)
    {
        var range = ParseRange(rangeText);
        var version = ParseVersion(versionText);
        return range.Contains(version);
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