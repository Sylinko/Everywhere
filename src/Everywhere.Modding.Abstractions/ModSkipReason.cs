namespace Everywhere.Modding.Abstractions;

/// <summary>
/// Explains why an enabled mod has no activation without treating it as a failed mod.
/// </summary>
public enum ModSkipReason
{
    /// <summary>
    /// The package has no entry matching the current runtime identifier.
    /// </summary>
    UnsupportedPlatform
}