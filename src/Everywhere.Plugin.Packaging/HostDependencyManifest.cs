using System.Text.Json;

namespace Everywhere.Plugin.Packaging;

/// <summary>
/// Reads only the deployment metadata needed to select the host's declared managed references.
/// Other .deps.json fields belong to the .NET SDK and are intentionally ignored.
/// </summary>
internal sealed class HostDependencyManifest
{
    /// <summary>
    /// Gets the target selected by the host build.
    /// </summary>
    public required TargetSelection RuntimeTarget { get; init; }

    /// <summary>
    /// Gets target-specific assets keyed by target name, then library identity.
    /// </summary>
    public required Dictionary<string, Dictionary<string, TargetLibrary>> Targets { get; init; }

    /// <summary>
    /// Gets library origins keyed by library identity, including runtime-pack markers.
    /// </summary>
    public required Dictionary<string, LibraryOrigin> Libraries { get; init; }

    /// <summary>
    /// Identifies the dependency target used by this deployment.
    /// </summary>
    public sealed class TargetSelection
    {
        /// <summary>
        /// Gets the selected target name. An explicit null is reported by the host importer.
        /// </summary>
        public required string? Name { get; init; }
    }

    /// <summary>
    /// Describes the managed runtime assets contributed by one library to a target.
    /// </summary>
    public sealed class TargetLibrary
    {
        /// <summary>
        /// Gets or sets asset paths. Asset metadata is opaque because reference selection only needs the paths.
        /// Libraries without managed runtime assets leave this dictionary empty.
        /// </summary>
        public Dictionary<string, JsonElement> Runtime { get; set; } = [];

        /// <summary>
        /// Gets declared satellite paths and their deployment locales, separately from compilation references.
        /// </summary>
        public Dictionary<string, ResourceAsset> Resources { get; set; } = [];
    }

    /// <summary>
    /// Describes the locale directory used when deploying a resource asset from a package.
    /// </summary>
    public sealed class ResourceAsset
    {
        /// <summary>
        /// Gets the culture declared by the dependency manifest.
        /// </summary>
        public required string Locale { get; init; }
    }

    /// <summary>
    /// Identifies the origin of a library without duplicating the SDK's full dependency model.
    /// </summary>
    public sealed class LibraryOrigin
    {
        /// <summary>
        /// Gets the origin kind used to exclude framework runtime packs from host references.
        /// </summary>
        public string? Type { get; init; }
    }
}