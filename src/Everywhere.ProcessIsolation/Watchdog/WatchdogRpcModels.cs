using MessagePack;

namespace Everywhere.ProcessIsolation.Watchdog;

/// <summary>
/// Process identity captured by Main before it awaits Watchdog startup. Windows
/// sends a Main-owned process-handle value for one cross-process duplication;
/// Unix sends PID plus start time to reject a reused PID.
/// </summary>
[MessagePackObject]
public sealed partial class RegisterWatchdogProcessRequest
{
    /// <summary>Process ID used for diagnostics and Unix process lookup.</summary>
    [Key(0)]
    public required int ProcessId { get; init; }

    /// <summary>Windows handle value valid in Main; zero on other platforms.</summary>
    [Key(1)]
    public long SourceProcessHandle { get; init; }

    /// <summary>UTC start-time ticks used to bind a Unix lookup to one process incarnation.</summary>
    [Key(2)]
    public long ProcessStartTimeUtcTicks { get; init; }
}

/// <summary>Result of capturing a process in the Watchdog.</summary>
[MessagePackObject]
public sealed partial class RegisterWatchdogProcessResponse
{
    /// <summary>Whether the target was still alive and could be captured.</summary>
    [Key(0)]
    public required bool Registered { get; init; }

    /// <summary>Opaque connection-scoped handle; zero when registration failed.</summary>
    [Key(1)]
    public required ulong RegistrationHandle { get; init; }
}

/// <summary>Releases the exact registration returned by <see cref="RegisterWatchdogProcessResponse"/>.</summary>
[MessagePackObject]
public sealed partial class UnregisterWatchdogProcessRequest
{
    /// <summary>Opaque registration handle returned by the same connection.</summary>
    [Key(0)]
    public required ulong RegistrationHandle { get; init; }

    /// <summary>Whether Watchdog should terminate the captured process before release.</summary>
    [Key(1)]
    public bool KillIfRunning { get; init; }
}

/// <summary>Idempotent result of releasing a registration.</summary>
[MessagePackObject]
public sealed partial class UnregisterWatchdogProcessResponse
{
    /// <summary>Whether the handle still identified a live registration.</summary>
    [Key(0)]
    public required bool Found { get; init; }
}