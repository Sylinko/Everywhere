namespace Everywhere.Modding.Packaging;

/// <summary>
/// Declares license and bundled third-party notice metadata inside a mod package manifest.
/// </summary>
public sealed record ModLicense
{
    /// <summary>
    /// Gets the SPDX license expression or custom <c>LicenseRef-</c> identifier.
    /// </summary>
    public required string Spdx { get; init; }

    /// <summary>
    /// Gets the optional package-relative license text path.
    /// </summary>
    public string? File { get; init; }

    /// <summary>
    /// Gets or sets whether explicit user acceptance is required before activating or updating this mod.
    /// </summary>
    public bool IsAcceptanceRequired { get; set; }

    /// <summary>
    /// Gets or sets package-relative paths of third-party license or notice files bundled with this mod.
    /// </summary>
    public string[] ThirdPartyFiles { get; set; } = [];
}

/// <summary>
/// Summarizes license terms and confirmation policy inside a static repository index entry.
/// </summary>
public sealed record ModIndexLicense
{
    /// <summary>
    /// Gets the SPDX license expression or custom <c>LicenseRef-</c> identifier.
    /// </summary>
    public required string Spdx { get; init; }

    /// <summary>
    /// Gets or sets whether explicit user acceptance is required before installing or updating this mod.
    /// </summary>
    public bool IsAcceptanceRequired { get; set; }
}
