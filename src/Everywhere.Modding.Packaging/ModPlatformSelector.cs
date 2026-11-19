using System.Text.Json;

namespace Everywhere.Modding.Packaging;

/// <summary>
/// Shares the build SDK's embedded portable RID graph between tooling and runtime selection.
/// </summary>
public static class ModPlatformSelector
{
    private static readonly IReadOnlyDictionary<string, string[]> Imports = ReadGraph();

    /// <summary>
    /// Returns whether the declaration uses a RID in the embedded portable graph.
    /// </summary>
    public static bool IsKnownPlatform(string platform) => Imports.ContainsKey(platform);

    /// <summary>
    /// Selects an entry, or returns null for an unsupported platform, before compatibility checks; host-range failure must not trigger a broader fallback.
    /// </summary>
    public static UniversalModEntry? Select(ModManifest manifest, string runtimeIdentifier)
    {
        ModManifestFile.Validate(manifest);

        switch (manifest.Entry)
        {
            case UniversalModEntry universalModEntry:
            {
                return universalModEntry;
            }
            case PlatformModEntry platformModEntry:
            {
                var variants = platformModEntry.Variants.ToDictionary(v => v.Platform, StringComparer.Ordinal);
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

        return null;
    }

    /// <summary>
    /// Converts a selected variant to the common execution model without retaining foreign variants.
    /// </summary>
    internal static UniversalModEntry GetImplementation(ModPlatformVariant variant)
    {
        return new UniversalModEntry
        {
            Assembly = variant.Assembly,
            Type = variant.Type,
            Framework = variant.Framework,
            ModApiVersion = variant.ModApiVersion,
            HostVersion = variant.HostVersion,
            Assemblies = variant.Assemblies,
            NativeLibraries = variant.NativeLibraries,
            ContentFiles = variant.ContentFiles,
            Dependencies = variant.Dependencies
        };
    }

    /// <summary>
    /// Reads the graph embedded at build time, avoiding dependence on an SDK installed on the user's machine.
    /// </summary>
    private static Dictionary<string, string[]> ReadGraph()
    {
        using var stream = typeof(ModPlatformSelector).Assembly
                .GetManifestResourceStream("Everywhere.Modding.Packaging.PortableRuntimeIdentifierGraph.json") ??
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