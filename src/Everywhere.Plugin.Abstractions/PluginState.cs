namespace Everywhere.Plugin.Abstractions;

/// <summary>
/// Describes lifecycle progress independently of desired enablement and assembly residency.
/// </summary>
public enum PluginState
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
}