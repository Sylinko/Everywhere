using Everywhere.Automation;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>
/// Owns one Host-retained pre-publication visual anchor and its initial scalar observation.
/// </summary>
public sealed class RemoteVisualAnchor : RpcSafeHandle
{
    /// <summary>
    /// Gets the remote visual Context that owns this anchor.
    /// </summary>
    public RemoteVisualContext Context { get; }

    /// <summary>
    /// Gets the initial bounded scalar snapshot returned when the anchor was acquired.
    /// </summary>
    public VisualElementSnapshot Snapshot => _observation.ToSnapshot();

    /// <summary>
    /// Gets the requested fields returned by the provider.
    /// </summary>
    public VisualElementFields AvailableFields => _observation.AvailableFields;

    /// <summary>
    /// Gets the requested fields unavailable from the provider.
    /// </summary>
    public VisualElementFields MissingFields => _observation.MissingFields;

    /// <summary>
    /// Gets the normalized provider-wide failure classification.
    /// </summary>
    public VisualElementQueryFailureKind? FailureKind => _observation.FailureKind;

    /// <summary>
    /// Gets the connection-scoped parent Context resource ID that owns this anchor.
    /// </summary>
    public long ContextId => _contextLease.ResourceId;

    private readonly RpcSafeHandleLease _contextLease;
    private readonly AcquireAutomationAnchorResponse _observation;
    private volatile bool _isConsumed;

    internal RemoteVisualAnchor(
        RemoteVisualContext context,
        RpcSafeHandleLease contextLease,
        AcquireAutomationAnchorResponse observation,
        long resourceId,
        RpcSafeHandleReleaseQueue releaseQueue
    ) : base(resourceId, releaseQueue)
    {
        Context = context;
        _contextLease = contextLease;
        _observation = observation;
    }

    /// <summary>
    /// Moves this ownership token into another Context on the same Automation Host connection.
    /// </summary>
    /// <remarks>
    /// A successful cross-Context move closes this source Anchor and returns the destination owner. Moving to the same Context is a no-op.
    /// If the connection is lost after Host commitment but before acknowledgment, the outcome is necessarily unknown.
    /// </remarks>
    public ValueTask<RemoteVisualAnchor> MoveAsync(
        RemoteVisualContext destinationContext,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default) =>
        Context.MoveAnchorAsync(this, destinationContext, query, cancellationToken);

    internal void Consume()
    {
        _isConsumed = true;
        Dispose();
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        try
        {
            return _isConsumed || base.ReleaseHandle();
        }
        finally
        {
            _contextLease.Dispose();
        }
    }
}