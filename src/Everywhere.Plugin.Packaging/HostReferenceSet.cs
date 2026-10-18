using System.Reflection;
using System.Text.Json;
using Everywhere.Plugin.Abstractions;

namespace Everywhere.Plugin.Packaging;

/// <summary>
/// Imports declared managed deployment assets without recursively discovering arbitrary installed DLLs.
/// Source exports and installed applications share deployment ownership; only installation imports
/// use the non-framework assemblies directly as compilation references.
/// </summary>
public sealed class HostReferenceSet
{
    /// <summary>
    /// Gets the descriptor filename emitted by plugin-enabled host builds.
    /// </summary>
    public const string DescriptorFileName = "plugin-host.json";

    /// <summary>
    /// Gets the host build and public contract identity.
    /// </summary>
    public PluginHostDescriptor Descriptor { get; }

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
    /// Reads an explicitly plugin-enabled distribution. Missing import metadata is an unsupported-host error.
    /// </summary>
    public HostReferenceSet(string hostDirectory)
    {
        Directory = Path.GetFullPath(
            hostDirectory.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ?
                Path.Combine(hostDirectory, "Contents", "MonoBundle") :
                hostDirectory);
        var descriptorPath = Path.Combine(Directory, DescriptorFileName);
        if (!File.Exists(descriptorPath))
        {
            throw new PluginValidationException("UnsupportedHost", $"'{hostDirectory}' has no plugin host import descriptor.");
        }

        using var descriptorStream = File.OpenRead(descriptorPath);
        Descriptor = JsonSerializer.Deserialize(descriptorStream, DescriptorJsonContext.PluginHostDescriptor) ??
            throw new PluginValidationException("UnsupportedHost", "The host descriptor is empty.");
        if (Descriptor.SchemaVersion != 1 || !PluginPlatformSelector.IsKnownPlatform(Descriptor.RuntimeIdentifier))
        {
            throw new PluginValidationException("UnsupportedHost", "The host import protocol or runtime identifier is unsupported.");
        }

        PluginCompatibility.ParseVersion(Descriptor.HostVersion);
        PluginCompatibility.ParseVersion(Descriptor.PluginApiVersion);
        PackagePath.ValidateRelative(Descriptor.EntryAssembly);

        var depsPath = PackagePath.Resolve(Directory, Path.ChangeExtension(Descriptor.EntryAssembly, ".deps.json"));
        using var depsStream = File.OpenRead(depsPath);
        var dependencies = JsonSerializer.Deserialize(depsStream, DependencyJsonContext.HostDependencyManifest) ??
            throw new PluginValidationException("InvalidHostDependencies", "The host dependency manifest is empty.");
        var targetName = dependencies.RuntimeTarget.Name ??
            throw new PluginValidationException("InvalidHostDependencies", "The host has no declared runtime target.");
        var target = dependencies.Targets[targetName];
        var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var runtimeAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resourceAssemblies = new List<string>();

        foreach (var (library, assets) in target)
        {
            var isRuntimePack = dependencies.Libraries.TryGetValue(library, out var origin) && origin.Type == "runtimepack";

            foreach (var asset in assets.Runtime.Keys)
            {
                var relative = PackagePath.Resolve(Directory, asset);
                var flat = Path.Combine(Directory, Path.GetFileName(asset));
                var path = File.Exists(relative) ? relative : flat;
                if (!File.Exists(path))
                {
                    throw new PluginValidationException("MissingHostAsset", $"Declared host asset '{asset}' is missing.");
                }

                var identity = AssemblyName.GetAssemblyName(path);
                var name = identity.Name ?? throw new PluginValidationException("InvalidHostAsset", path);
                if (runtimeAssemblies.TryGetValue(name, out var existing) && existing != path)
                {
                    throw new PluginValidationException("AmbiguousHostAsset", $"Host assembly '{name}' resolves to different files.");
                }

                runtimeAssemblies[name] = path;
                if (!isRuntimePack) assemblies[name] = path;
            }

            foreach (var (asset, resource) in assets.Resources)
            {
                var relative = PackagePath.Resolve(Directory, asset);
                var deployed = PackagePath.Resolve(Directory, resource.Locale + "/" + Path.GetFileName(asset));
                var path = File.Exists(relative) ? relative : deployed;
                if (!File.Exists(path))
                {
                    throw new PluginValidationException("MissingHostAsset", $"Declared host resource '{asset}' is missing.");
                }

                resourceAssemblies.Add(path);
            }
        }

        var contract = typeof(IPlugin).Assembly.GetName().Name ?? "Everywhere.Plugin.Abstractions";
        if (!assemblies.ContainsKey(contract))
        {
            throw new PluginValidationException("MissingPluginContract", "The host does not declare its plugin abstraction assembly.");
        }

        Assemblies = assemblies;
        RuntimeAssemblies = runtimeAssemblies;
        ResourceAssemblies = resourceAssemblies;
    }
}