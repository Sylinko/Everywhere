namespace Everywhere.Plugin.Hosting;

/// <summary>
/// Describes the outcome independently of desired configuration or physical assembly residency.
/// </summary>
public enum PluginApplyStatus
{
    /// <summary>
    /// The requested set is active.
    /// </summary>
    Applied,

    /// <summary>
    /// Preflight rejected the selection before existing plugins were stopped.
    /// </summary>
    Rejected,

    /// <summary>
    /// Activation failed and its new ownership was successfully cleaned up and collected.
    /// </summary>
    ActivationFailed,

    /// <summary>
    /// Shutdown, cleanup or collection is unconfirmed; further mutations require restart.
    /// </summary>
    RestartRequired
}

/// <summary>
/// Carries host-owned diagnostic values without retaining collectible exception or reflection objects.
/// </summary>
public sealed record PluginDiagnostic
{
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
public sealed record PluginApplyResult
{
    /// <summary>
    /// Gets the outcome of the requested change.
    /// </summary>
    public required PluginApplyStatus Status { get; init; }

    /// <summary>
    /// Gets initialized activations that have not successfully stopped.
    /// </summary>
    public required string[] ActivePluginIds { get; init; }

    /// <summary>
    /// Gets loaded or unconfirmed collectible owner names.
    /// </summary>
    public required string[] ResidentAssemblyNames { get; init; }

    /// <summary>
    /// Gets failures and selection diagnostics as host-owned values.
    /// </summary>
    public required PluginDiagnostic[] Diagnostics { get; init; }
}