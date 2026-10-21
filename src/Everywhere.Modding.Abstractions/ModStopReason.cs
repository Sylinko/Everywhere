namespace Everywhere.Modding.Abstractions;

/// <summary>
/// Identifies whether an activation is retiring in a live host or the host process is exiting.
/// </summary>
public enum ModStopReason
{
    /// <summary>
    /// Retires an activation, including replacement or completed initialization-failure cleanup.
    /// </summary>
    Unload,

    /// <summary>
    /// Disposes owned resources for process exit without requiring UI dispatch or reversing registrations.
    /// </summary>
    Exit
}