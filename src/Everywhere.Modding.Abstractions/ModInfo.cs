namespace Everywhere.Modding.Abstractions;

/// <summary>
/// Describes a mod using host-owned values only; a snapshot does not retain the activation.
/// </summary>
public sealed record ModInfo
{
    /// <summary>
    /// Gets the stable mod identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the mod's semantic version, independent of its assembly version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets whether the desired configuration requests this mod to be enabled.
    /// </summary>
    public required bool IsEnabled { get; init; }

    /// <summary>
    /// Gets lifecycle progress, not physical assembly collection status.
    /// </summary>
    public required ModState State { get; init; }

    /// <summary>
    /// Gets the reason when State is Skipped; otherwise null.
    /// </summary>
    public ModSkipReason? SkipReason { get; init; }
}