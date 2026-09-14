using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Owns one Host-side interactive picker and its current native candidate.</summary>
public sealed class RemoteVisualPicker : RpcSafeHandle
{
    /// <summary>Gets the remote visual Context that owns this picker.</summary>
    public RemoteVisualContext Context { get; }

    internal long ContextId => _contextLease.ResourceId;

    private readonly IAutomationHostRpc _rpc;
    private readonly RpcSafeHandleLease _contextLease;
    private readonly RpcSafeHandleReleaseQueue _releaseQueue;
    private long _nextRevision;

    internal RemoteVisualPicker(
        IAutomationHostRpc rpc,
        RemoteVisualContext context,
        RpcSafeHandleLease contextLease,
        long resourceId,
        RpcSafeHandleReleaseQueue releaseQueue
    ) : base(resourceId, releaseQueue)
    {
        Context = context;
        _rpc = rpc;
        _contextLease = contextLease;
        _releaseQueue = releaseQueue;
    }

    /// <summary>Moves the Host picker to one physical screen point and returns its current scalar observation.</summary>
    public async ValueTask<VisualPickerObservation> UpdateAsync(
        PixelPoint point,
        ScreenSelectionMode mode,
        CancellationToken cancellationToken = default)
    {
        using var pickerLease = AcquireLease();
        var revision = Interlocked.Increment(ref _nextRevision);
        return await _rpc.UpdatePickerAsync(
            new UpdateVisualPickerRequest
            {
                ContextId = ContextId,
                PickerId = pickerLease.ResourceId,
                Revision = revision,
                PointX = point.X,
                PointY = point.Y,
                Mode = mode,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Consumes this picker and transfers the exact observed candidate into a new remote visual anchor.</summary>
    public async ValueTask<RemoteVisualAnchor?> ConfirmAsync(
        VisualPickerObservation observation,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var pickerLease = AcquireLease();
        try
        {
            var contextLease = Context.AcquireLease();
            var anchorId = _releaseQueue.AllocateResourceId();
            try
            {
                var requestedQuery = query ?? VisualElementQueryRequest.Default;
                var response = await _rpc.ConfirmPickerAsync(
                    new ConfirmVisualPickerRequest
                    {
                        ContextId = ContextId,
                        PickerId = pickerLease.ResourceId,
                        AnchorId = anchorId,
                        Revision = observation.Revision,
                        RequestedFields = requestedQuery.RequestedFields,
                        MaxTextCharacters = requestedQuery.MaxTextCharacters,
                    },
                    cancellationToken).ConfigureAwait(false);
                if (response.IsAvailable)
                {
                    return new RemoteVisualAnchor(Context, contextLease, response, anchorId, _releaseQueue);
                }

                _releaseQueue.TryQueueRelease(anchorId);
                contextLease.Dispose();
                return null;
            }
            catch
            {
                _releaseQueue.TryQueueRelease(anchorId);
                contextLease.Dispose();
                throw;
            }
        }
        finally
        {
            Dispose();
        }
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        try
        {
            return base.ReleaseHandle();
        }
        finally
        {
            _contextLease.Dispose();
        }
    }
}