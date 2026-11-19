namespace Everywhere.Modding.Packaging;

/// <summary>
/// Describes one platform-specific implementation, including its complete managed selection.
/// </summary>
public sealed record ModPlatformVariant : UniversalModEntry
{
    /// <summary>
    /// Gets the portable .NET runtime identifier.
    /// </summary>
    public required string Platform { get; init; }
}
