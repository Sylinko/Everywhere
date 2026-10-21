namespace Everywhere.Modding.Abstractions;

/// <summary>
/// Describes lifecycle progress independently of desired enablement and assembly residency.
/// </summary>
public enum ModState
{
    /// <summary>
    /// No initialized activation is running.
    /// </summary>
    Inactive,

    /// <summary>
    /// Initialization is in progress.
    /// </summary>
    Initializing,

    /// <summary>
    /// Initialization completed successfully.
    /// </summary>
    Active,

    /// <summary>
    /// Shutdown or resource cleanup is in progress.
    /// </summary>
    Stopping,

    /// <summary>
    /// An activation or shutdown operation failed.
    /// </summary>
    Failed,

    /// <summary>
    /// The desired mod has no applicable entry on this host. No mod code was loaded.
    /// </summary>
    Skipped,

    /// <summary>
    /// A selected dependency failed validation or activation, so this mod was not started.
    /// </summary>
    Blocked
}