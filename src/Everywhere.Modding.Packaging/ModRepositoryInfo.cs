namespace Everywhere.Modding.Packaging;

/// <summary>
/// Describes a mod's public source repository web page for display purposes only.
/// </summary>
public sealed record ModRepositoryInfo
{
    /// <summary>
    /// Gets the HTTP(S) web URL of the repository.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the optional relative POSIX directory containing the mod project inside the repository.
    /// </summary>
    public string? Directory { get; init; }
}
