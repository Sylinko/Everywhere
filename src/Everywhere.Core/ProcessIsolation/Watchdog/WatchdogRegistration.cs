namespace Everywhere.ProcessIsolation.Watchdog;

/// <summary>
/// Main-side ownership token for one Watchdog registration. Disposing the token
/// is the only supported unregistration path, so a recycled PID can never target
/// a different registration.
/// </summary>
public sealed class WatchdogRegistration : IAsyncDisposable
{
    /// <summary>Captured Main-side identity reused when a replacement Watchdog reconnects.</summary>
    internal RegisterWatchdogProcessRequest Request { get; }

    /// <summary>Opaque handle owned by the current Watchdog connection generation.</summary>
    internal ulong RemoteHandle { get; set; }

    private readonly Lock _disposeGate = new();
    private readonly WatchdogManager _manager;
    private readonly IDisposable _sourceProcessLease;

    private Task? _disposeTask;

    internal WatchdogRegistration(
        WatchdogManager manager,
        IDisposable sourceProcessLease,
        RegisterWatchdogProcessRequest request,
        ulong remoteHandle)
    {
        _manager = manager;
        _sourceProcessLease = sourceProcessLease;
        Request = request;
        RemoteHandle = remoteHandle;
    }

    /// <summary>
    /// Releases this registration once. The optional value overrides the fluent
    /// default for this disposal; the first disposal request owns the outcome.
    /// </summary>
    public ValueTask DisposeAsync(bool killOnDispose = true)
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= _manager.ReleaseAsync(this, killOnDispose));
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => DisposeAsync();

    /// <summary>Releases the Main-owned source identity after remote cleanup.</summary>
    internal void ReleaseSourceProcessLease() => _sourceProcessLease.Dispose();
}