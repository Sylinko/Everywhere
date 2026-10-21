namespace Everywhere.Modding.Hosting;

/// <summary>
/// Describes the outcome independently of desired configuration or physical assembly residency.
/// </summary>
public enum ModApplyStatus
{
    /// <summary>
    /// The requested set is active.
    /// </summary>
    Applied,

    /// <summary>
    /// Some requested mods failed or were blocked; independent changes were applied.
    /// </summary>
    CompletedWithErrors,

    /// <summary>
    /// Shutdown, cleanup or collection is unconfirmed; further mutations require restart.
    /// </summary>
    RestartRequired
}

/// <summary>
/// Carries host-owned diagnostic values without retaining collectible exception or reflection objects.
/// </summary>
public sealed record ModDiagnostic
{
    /// <summary>
    /// Gets the affected mod, or null for a source or engine-wide failure.
    /// </summary>
    public string? ModId { get; init; } // TODO: null?

    /// <summary>
    /// Gets the stable code used by structured tools and application localization.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Gets diagnostic detail for logs, not a prelocalized persistent UI message.
    /// </summary>
    public required string Detail { get; init; }
}

/// <summary>
/// Reports an operation and the actual remaining runtime state.
/// </summary>
public sealed record ModApplyResult
{
    /// <summary>
    /// Gets the outcome of the requested change.
    /// </summary>
    public required ModApplyStatus Status { get; init; }

    /// <summary>
    /// Gets initialized activations that have not successfully stopped.
    /// </summary>
    public required string[] ActiveModIds { get; init; }

    /// <summary>
    /// Gets loaded or unconfirmed collectible owner names.
    /// </summary>
    public required string[] ResidentAssemblyNames { get; init; }

    /// <summary>
    /// Gets failures and selection diagnostics as host-owned values.
    /// </summary>
    public required ModDiagnostic[] Diagnostics { get; init; }
}