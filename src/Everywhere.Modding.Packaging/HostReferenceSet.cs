using System.Reflection;
using System.Text.Json;
using Everywhere.Modding.Abstractions;

namespace Everywhere.Modding.Packaging;

/// <summary>
/// Imports declared managed deployment assets without recursively discovering arbitrary installed DLLs.
/// Source exports and installed applications share deployment ownership; only installation imports
/// use the non-framework assemblies directly as compilation references.
/// </summary>
public sealed class HostReferenceSet
{
    /// <summary>
    /// Gets the descriptor filename emitted by mod-enabled host builds.
    /// </summary>
    public const string DescriptorFileName = "mod-host.json";

    /// <summary>
    /// Gets the host build and public contract identity.
    /// </summary>
    public ModHostDescriptor Descriptor { get; }

    /// <summary>
    /// Gets the resolved managed directory, including the known layout within a macOS app bundle.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Gets application and third-party managed references, excluding framework runtime-pack implementations.
    /// </summary>
    public IReadOnlyDictionary<string, string> Assemblies { get; }

    /// <summary>
    /// Gets all declared managed runtime assemblies, including framework runtime packs.
    /// Packaging uses this set to exclude host-provided runtime files without referencing them for compilation.
    /// </summary>
    public IReadOnlyDictionary<string, string> RuntimeAssemblies { get; }

    /// <summary>
    /// Gets resource assemblies declared by the selected deployment, used only for packaging exclusion.
    /// </summary>
    public IReadOnlyList<string> ResourceAssemblies { get; }

    private static readonly PackagingJsonSerializerContext DescriptorJsonContext = new(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectNullableAnnotations = true
        });

    private static readonly PackagingJsonSerializerContext DependencyJsonContext = new(
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            RespectNullableAnnotations = true
        });

    /// <summary>
    /// Reads an explicitly mod-enabled distribution. Missing import metadata is an unsupported-host error.
    /// Optional <paramref name="packageProbeDirectories"/> are searched only during build-time import from
    /// library hosts whose <c>package</c> assets remain in the NuGet package cache rather than copied locally.
    /// </summary>
    public HostReferenceSet(string hostDirectory, IReadOnlyList<string>? packageProbeDirectories = null)
    {
        Directory = Path.GetFullPath(
            hostDirectory.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(hostDirectory, "Contents", "MonoBundle")
                : hostDirectory);
        var probeDirectories = NormalizeProbeDirectories(packageProbeDirectories);
        var descriptorPath = Path.Combine(Directory, DescriptorFileName);
        if (!File.Exists(descriptorPath))
        {
            throw new ModValidationException("UnsupportedHost", $"'{hostDirectory}' has no mod host import descriptor.");
        }

        using var descriptorStream = File.OpenRead(descriptorPath);
        Descriptor = JsonSerializer.Deserialize(descriptorStream, DescriptorJsonContext.ModHostDescriptor) ??
            throw new ModValidationException("UnsupportedHost", "The host descriptor is empty.");
        if (Descriptor.SchemaVersion != 1 || !ModPlatformSelector.IsKnownPlatform(Descriptor.RuntimeIdentifier))
        {
            throw new ModValidationException("UnsupportedHost", "The host import protocol or runtime identifier is unsupported.");
        }

        ModCompatibility.ParseVersion(Descriptor.HostVersion);
        ModCompatibility.ParseVersion(Descriptor.ModApiVersion);
        PackagePath.ValidateRelative(Descriptor.EntryAssembly);

        var depsPath = PackagePath.Resolve(Directory, Path.ChangeExtension(Descriptor.EntryAssembly, ".deps.json"));
        using var depsStream = File.OpenRead(depsPath);
        var dependencies = JsonSerializer.Deserialize(depsStream, DependencyJsonContext.HostDependencyManifest) ??
            throw new ModValidationException("InvalidHostDependencies", "The host dependency manifest is empty.");
        var targetName = dependencies.RuntimeTarget.Name ??
            throw new ModValidationException("InvalidHostDependencies", "The host has no declared runtime target.");
        var target = dependencies.Targets[targetName];
        var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var runtimeAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resourceAssemblies = new List<string>();

        foreach (var (library, assets) in target)
        {
            dependencies.Libraries.TryGetValue(library, out var origin);
            var isRuntimePack = origin?.Type == "runtimepack";

            foreach (var asset in assets.Runtime.Keys)
            {
                var flat = Path.Combine(Directory, Path.GetFileName(asset));
                var path = ResolveAssetPath(asset, flat, origin, probeDirectories) ??
                    throw new ModValidationException("MissingHostAsset", $"Declared host asset '{asset}' is missing.");

                var identity = AssemblyName.GetAssemblyName(path);
                var name = identity.Name ?? throw new ModValidationException("InvalidHostAsset", path);
                if (runtimeAssemblies.TryGetValue(name, out var existing) && existing != path)
                {
                    throw new ModValidationException("AmbiguousHostAsset", $"Host assembly '{name}' resolves to different files.");
                }

                runtimeAssemblies[name] = path;
                if (!isRuntimePack) assemblies[name] = path;
            }

            foreach (var (asset, resource) in assets.Resources)
            {
                var deployed = PackagePath.Resolve(Directory, resource.Locale + "/" + Path.GetFileName(asset));
                var path = ResolveAssetPath(asset, deployed, origin, probeDirectories) ??
                    throw new ModValidationException("MissingHostAsset", $"Declared host resource '{asset}' is missing.");

                resourceAssemblies.Add(path);
            }
        }

        var contract = typeof(IMod).Assembly.GetName().Name ?? "Everywhere.Modding.Abstractions";
        if (!assemblies.ContainsKey(contract))
        {
            throw new ModValidationException("MissingModContract", "The host does not declare its mod abstraction assembly.");
        }

        Assemblies = assemblies;
        RuntimeAssemblies = runtimeAssemblies;
        ResourceAssemblies = resourceAssemblies;
    }

    private string? ResolveAssetPath(
        string asset,
        string fallbackPath,
        HostDependencyManifest.LibraryOrigin? origin,
        IReadOnlyList<string> probeDirectories
    )
    {
        var relative = PackagePath.Resolve(Directory, asset);
        if (File.Exists(relative)) return relative;
        if (File.Exists(fallbackPath)) return fallbackPath;

        if (probeDirectories.Count == 0 || origin is not { Type: "package", Path.Length: > 0 } packageOrigin)
        {
            return null;
        }

        foreach (var probeDirectory in probeDirectories)
        {
            var packageRoot = PackagePath.Resolve(probeDirectory, packageOrigin.Path);
            var candidate = PackagePath.Resolve(packageRoot, asset);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string[] NormalizeProbeDirectories(IReadOnlyList<string>? packageProbeDirectories)
    {
        if (packageProbeDirectories is not { Count: > 0 }) return [];

        return packageProbeDirectories
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(System.IO.Directory.Exists)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }
}
