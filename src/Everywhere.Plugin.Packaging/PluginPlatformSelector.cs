using System.Text.Json;

namespace Everywhere.Plugin.Packaging;

/// <summary>
/// Shares the build SDK's embedded portable RID graph between tooling and runtime selection.
/// </summary>
public static class PluginPlatformSelector
{
    private static readonly IReadOnlyDictionary<string, string[]> Imports = ReadGraph();

    /// <summary>
    /// Returns whether the declaration uses a RID in the embedded portable graph.
    /// </summary>
    public static bool IsKnownPlatform(string platform) => Imports.ContainsKey(platform);

    /// <summary>
    /// Selects an entry before compatibility checks; host-range failure must not trigger a broader fallback.
    /// </summary>
    public static UniversalPluginEntry Select(PluginManifest manifest, string runtimeIdentifier)
    {
        PluginManifestFile.Validate(manifest);

        switch (manifest.Entry)
        {
            case UniversalPluginEntry universalPluginEntry:
            {
                return universalPluginEntry;
            }
            case PlatformPluginEntry platformPluginEntry:
            {
                var variants = platformPluginEntry.Variants.ToDictionary(v => v.Platform, StringComparer.Ordinal);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Queue<string>();
                pending.Enqueue(runtimeIdentifier);

                while (pending.TryDequeue(out var platform))
                {
                    if (!visited.Add(platform)) continue;

                    if (variants.TryGetValue(platform, out var variant))
                    {
                        return GetImplementation(variant);
                    }

                    if (!Imports.TryGetValue(platform, out var imports)) continue;

                    foreach (var import in imports)
                    {
                        pending.Enqueue(import);
                    }
                }
                break;
            }
        }

        throw new PluginValidationException("UnsupportedPlatform", $"Plugin '{manifest.Id}' does not support '{runtimeIdentifier}'.");
    }

    /// <summary>
    /// Converts a selected variant to the common execution model without retaining foreign variants.
    /// </summary>
    internal static UniversalPluginEntry GetImplementation(PluginPlatformVariant variant)
    {
        return new UniversalPluginEntry
        {
            Assembly = variant.Assembly,
            Type = variant.Type,
            Framework = variant.Framework,
            PluginApiVersion = variant.PluginApiVersion,
            HostVersion = variant.HostVersion,
            Assemblies = variant.Assemblies,
            Dependencies = variant.Dependencies
        };
    }

    /// <summary>
    /// Reads the graph embedded at build time, avoiding dependence on an SDK installed on the user's machine.
    /// </summary>
    private static Dictionary<string, string[]> ReadGraph()
    {
        using var stream = typeof(PluginPlatformSelector).Assembly
                .GetManifestResourceStream("Everywhere.Plugin.Packaging.PortableRuntimeIdentifierGraph.json") ??
            throw new InvalidOperationException("The portable RID graph was not embedded in the runtime.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("runtimes").EnumerateObject().ToDictionary(
            item => item.Name,
            item => item.Value.TryGetProperty("#import", out var imports) ?
                imports.EnumerateArray().Select(value => value.GetString() ?? "").ToArray() :
                [],
            StringComparer.Ordinal);
    }
}