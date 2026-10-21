using System.Text.Json.Serialization;

namespace Everywhere.Modding.Packaging;

/// <summary>
/// Selects exactly one lifecycle entry for a mod activation.
/// </summary>
[JsonDerivedType(typeof(UniversalModEntry), "universal")]
[JsonDerivedType(typeof(PlatformModEntry), "perPlatform")]
public abstract record ModEntry;

/// <summary>
/// Describes an entry whose declared assets apply without platform selection.
/// </summary>
public sealed record UniversalModEntry : ModEntry
{
    /// <summary>
    /// Gets the package-relative entry DLL path.
    /// </summary>
    public required string Assembly { get; init; }

    /// <summary>
    /// Gets the full lifecycle class name.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the target framework compiled into this entry.
    /// </summary>
    public required string Framework { get; init; }

    /// <summary>
    /// Gets the accepted public mod API version range.
    /// </summary>
    public required string ModApiVersion { get; init; }

    /// <summary>
    /// Gets the permitted host version range when host implementation APIs are used.
    /// </summary>
    public string? HostVersion { get; init; }

    /// <summary>
    /// Gets all managed runtime files selected with this entry, including its own assembly.
    /// </summary>
    public required string[] Assemblies { get; init; }

    /// <summary>
    /// Gets selected native libraries resolved by filename within this entry's managed owners.
    /// </summary>
    public string[] NativeLibraries { get; set; } = [];

    /// <summary>
    /// Gets supporting files, including executables, that are installed but never loaded as assemblies.
    /// </summary>
    public string[] ContentFiles { get; set; } = [];

    /// <summary>
    /// Gets or sets required and optional logical dependencies for this entry.
    /// </summary>
    public ModDependency[] Dependencies { get; set; } = [];
}

/// <summary>
/// Describes mutually exclusive platform implementations of one mod.
/// </summary>
public sealed record PlatformModEntry : ModEntry
{
    /// <summary>
    /// Gets the platform variants. Array order does not determine selection.
    /// </summary>
    public required ModPlatformVariant[] Variants { get; init; }
}