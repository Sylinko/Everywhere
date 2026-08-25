using System.Diagnostics;

namespace Everywhere.ProcessIsolation.Watchdog;

/// <summary>Coordinates registration of application-owned subprocesses with the Watchdog.</summary>
public interface IWatchdogManager
{
    /// <summary>
    /// Captures a process by PID and registers that exact process incarnation.
    /// </summary>
    /// <param name="processId">The process ID to monitor.</param>
    /// <returns>A registration lease, or <see langword="null"/> when Watchdog is unavailable or the process has exited.</returns>
    Task<WatchdogRegistration?> RegisterProcessAsync(int processId);

    /// <summary>
    /// Captures the supplied process before awaiting Watchdog startup. On Windows
    /// this preserves the caller's process handle, avoiding PID reuse entirely.
    /// </summary>
    /// <param name="process">Process to monitor.</param>
    /// <returns>A registration lease, or <see langword="null"/> when Watchdog is unavailable or the process has exited.</returns>
    Task<WatchdogRegistration?> RegisterProcessAsync(Process process);
}