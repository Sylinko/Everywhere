namespace Everywhere.Plugin.Packaging;

/// <summary>
/// Describes one platform-specific implementation, including its complete managed selection.
/// </summary>
public sealed record PluginPlatformVariant
{
    /// <summary>
    /// Gets the portable .NET runtime identifier.
    /// </summary>
    public required string Platform { get; init; }

    /// <summary>
    /// Gets the package-relative entry assembly path.
    /// </summary>
    public required string Assembly { get; init; }

    /// <summary>
    /// Gets the full lifecycle class name.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the entry target framework.
    /// </summary>
    public required string Framework { get; init; }

    /// <summary>
    /// Gets the accepted public plugin API range.
    /// </summary>
    public required string PluginApiVersion { get; init; }

    /// <summary>
    /// Gets the accepted Everywhere host range when required by implementation references.
    /// </summary>
    public string? HostVersion { get; init; }

    /// <summary>
    /// Gets all selected managed files, including common supporting files and the entry DLL.
    /// </summary>
    public required string[] Assemblies { get; init; }

    /// <summary>
    /// Gets or sets dependencies applicable to this platform entry only.
    /// </summary>
    public PluginDependency[] Dependencies { get; set; } = [];
}