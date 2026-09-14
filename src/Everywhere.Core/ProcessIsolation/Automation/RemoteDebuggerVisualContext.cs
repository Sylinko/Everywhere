using Everywhere.Automation;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Remote visual Context handle that additionally exposes developer inspection operations.</summary>
public sealed class RemoteDebuggerVisualContext : RemoteVisualContext
{
    private readonly IAutomationHostDiagnosticsRpc _diagnosticsRpc;

    internal RemoteDebuggerVisualContext(
        Guid id,
        long resourceId,
        IAutomationHostRpc rpc,
        IAutomationHostDiagnosticsRpc diagnosticsRpc,
        RpcSafeHandleReleaseQueue releaseQueue
    ) : base(id, resourceId, rpc, releaseQueue)
    {
        _diagnosticsRpc = diagnosticsRpc;
    }

    /// <summary>Builds and publishes one bounded diagnostic tree around an existing visual anchor.</summary>
    public async ValueTask<AutomationVisualTreeResponse> InspectAnchorAsync(
        RemoteVisualAnchor anchor,
        int maximumNodes = InspectAutomationAnchorRequest.DefaultMaximumNodes,
        CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(anchor.Context, this))
        {
            throw new ArgumentException("The anchor must belong to this remote debugger Context.", nameof(anchor));
        }

        using var contextLease = AcquireLease();
        using var anchorLease = anchor.AcquireLease();
        return await _diagnosticsRpc.InspectAnchorAsync(
            new InspectAutomationAnchorRequest
            {
                ContextId = contextLease.ResourceId,
                AnchorId = anchorLease.ResourceId,
                MaximumNodes = maximumNodes,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns a fresh field-selected observation of one published diagnostic target.</summary>
    public async ValueTask<VisualElementSnapshot> GetTargetSnapshotAsync(
        int targetId,
        VisualElementFields requestedFields,
        int maxTextCharacters = 0,
        CancellationToken cancellationToken = default)
    {
        using var contextLease = AcquireLease();
        var response = await _diagnosticsRpc.GetTargetSnapshotAsync(
            new GetAutomationTargetSnapshotRequest
            {
                ContextId = contextLease.ResourceId,
                TargetId = targetId,
                RequestedFields = requestedFields,
                MaxTextCharacters = maxTextCharacters,
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsAvailable)
        {
            throw new InvalidOperationException($"Visual target {targetId} is no longer available.");
        }

        return response.ToSnapshot();
    }

    /// <summary>Builds selected diagnostic targets and returns the Host-written local file path.</summary>
    public async ValueTask<string> WriteVisualTreeFileAsync(
        IReadOnlyList<int> targetIds,
        int targetTokenBudget,
        CancellationToken cancellationToken = default)
    {
        using var contextLease = AcquireLease();
        var response = await _diagnosticsRpc.WriteVisualTreeFileAsync(
            new WriteAutomationVisualTreeFileRequest
            {
                ContextId = contextLease.ResourceId,
                TargetIds = [.. targetIds],
                TargetTokenBudget = targetTokenBudget,
            },
            cancellationToken).ConfigureAwait(false);
        return response.FilePath;
    }
}