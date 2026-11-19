namespace Everywhere.Modding.Packaging;

/// <summary>
/// Adds the small mod import contract that an application's existing dependency file does not describe.
/// </summary>
public sealed record ModHostDescriptor
{
    /// <summary>
    /// Gets or sets the import protocol version supported by this host distribution.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Gets the exact Everywhere build version.
    /// </summary>
    public required string HostVersion { get; init; }

    /// <summary>
    /// Gets the public mod contract version provided by the host.
    /// </summary>
    public required string ModApiVersion { get; init; }

    /// <summary>
    /// Gets the host target framework.
    /// </summary>
    public required string Framework { get; init; }

    /// <summary>
    /// Gets the portable process runtime identifier for this distribution.
    /// </summary>
    public required string RuntimeIdentifier { get; init; }

    /// <summary>
    /// Gets the platform host entry filename, independent of the project name that produced it.
    /// </summary>
    public required string EntryAssembly { get; init; }
}