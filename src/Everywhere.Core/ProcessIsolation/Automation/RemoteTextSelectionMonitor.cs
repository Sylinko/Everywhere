using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Contains the result and optional handle returned by a monitor start request.</summary>
public sealed record RemoteTextSelectionMonitorStartResult(
    TextSelectionMonitoringControlResult Result,
    RemoteTextSelectionMonitor? Monitor
);

/// <summary>Owns one Host-side native text-selection monitor.</summary>
public sealed class RemoteTextSelectionMonitor : RpcSafeHandle
{
    /// <summary>Gets the remote Context resource ID.</summary>
    public long ContextId => _contextLease.ResourceId;

    /// <summary>Gets the remote monitor resource ID.</summary>
    public long MonitorId { get; }

    private readonly RpcSafeHandleLease _contextLease;
    private readonly IAutomationHostRpc _rpc;
    private int _isContextLeaseReleased;

    internal RemoteTextSelectionMonitor(
        IAutomationHostRpc rpc,
        RpcSafeHandleLease contextLease,
        long monitorId,
        RpcSafeHandleReleaseQueue releaseQueue) : base(monitorId, releaseQueue)
    {
        _rpc = rpc;
        _contextLease = contextLease;
        MonitorId = monitorId;
    }

    /// <summary>Stops native listening and awaits Host cleanup.</summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (IsClosed || IsInvalid) return;
        using var lease = AcquireLease();
        await _rpc.StopTextSelectionMonitoringAsync(
            new StopTextSelectionMonitoringRequest { MonitorId = lease.ResourceId },
            cancellationToken).ConfigureAwait(false);
        SetHandleAsInvalid();
        ReleaseContextLease();
    }

    /// <summary>Applies a complete replacement policy to this active monitor.</summary>
    public async ValueTask UpdateConfigurationAsync(
        TextSelectionMonitoringConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquireLease();
        await _rpc.UpdateTextSelectionMonitoringAsync(
            new UpdateTextSelectionMonitoringRequest
            {
                MonitorId = lease.ResourceId,
                Configuration = configuration,
            },
            cancellationToken).ConfigureAwait(false);
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            return base.ReleaseHandle();
        }
        finally
        {
            ReleaseContextLease();
        }
    }

    private void ReleaseContextLease()
    {
        if (Interlocked.Exchange(ref _isContextLeaseReleased, 1) == 0) _contextLease.Dispose();
    }
}