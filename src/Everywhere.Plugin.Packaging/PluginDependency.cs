namespace Everywhere.Plugin.Packaging;

/// <summary>
/// Declares a logical dependency and the assembly bindings it permits.
/// </summary>
public sealed record PluginDependency
{
    /// <summary>
    /// Gets the referenced plugin ID.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the explicitly permitted runtime version range.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets whether this entry can run without a compatible, enabled dependency.
    /// </summary>
    public bool IsOptional { get; init; }

    /// <summary>
    /// Gets the simple assembly names owned by the dependency that this consumer may bind.
    /// </summary>
    public required string[] Assemblies { get; init; }
}