using System.Text.Json;

namespace Everywhere.Modding.Packaging;

/// <summary>
/// Shares the build SDK's embedded portable RID graph between tooling and runtime selection.
/// </summary>
public static class ModPlatformSelector
{
    private static readonly Dictionary<string, string[]> Imports = ReadGraph();

    /// <summary>
    /// Returns whether the declaration uses a RID in the embedded portable graph.
    /// </summary>
    public static bool IsKnownPlatform(string platform) => Imports.ContainsKey(platform);

    /// <summary>
    /// Enumerates <paramref name="runtimeIdentifier"/> followed by its portable fallback chain in breadth-first order.
    /// </summary>
    public static IEnumerable<string> GetFallbackPlatforms(string runtimeIdentifier)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(runtimeIdentifier);

        while (pending.TryDequeue(out var platform))
        {
            if (!visited.Add(platform)) continue;

            yield return platform;

            if (!Imports.TryGetValue(platform, out var imports)) continue;

            foreach (var import in imports)
            {
                pending.Enqueue(import);
            }
        }
    }

    /// <summary>
    /// Selects an entry, or returns <see langword="null"/> for an unsupported platform, before compatibility checks;
    /// host-range failure must not trigger a broader fallback.
    /// </summary>
    public static UniversalModEntry? Select(ModManifest manifest, string runtimeIdentifier)
    {
        return Select(manifest.Entry, runtimeIdentifier);
    }

    /// <summary>
    /// Selects an entry directly from a universal or platform-specific <see cref="ModEntry"/> declaration.
    /// </summary>
    public static UniversalModEntry? Select(ModEntry entry, string runtimeIdentifier)
    {
        switch (entry)
        {
            case UniversalModEntry universalModEntry:
            {
                return universalModEntry;
            }
            case PlatformModEntry platformModEntry:
            {
                var variants = platformModEntry.Variants.ToDictionary(v => v.Platform, StringComparer.Ordinal);
                foreach (var platform in GetFallbackPlatforms(runtimeIdentifier))
                {
                    if (variants.TryGetValue(platform, out var variant))
                    {
                        return variant;
                    }
                }

                break;
            }
        }

        return null;
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
        return document.RootElement.GetProperty("runtimes")
            .EnumerateObject()
            .ToDictionary(
                item => item.Name,
                item => item.Value.TryGetProperty("#import", out var imports)
                    ? imports.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
                    : [],
                StringComparer.Ordinal);
    }
}
