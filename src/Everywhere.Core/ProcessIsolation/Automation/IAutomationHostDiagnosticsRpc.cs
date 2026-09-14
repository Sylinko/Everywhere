using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Developer-only visual inspection operations executed by the Automation Host.</summary>
[RpcContract(0x0201)]
public interface IAutomationHostDiagnosticsRpc
{
    /// <summary>Builds and publishes one bounded structured tree around an existing visual anchor.</summary>
    [RpcMethod(1)]
    ValueTask<AutomationVisualTreeResponse> InspectAnchorAsync(
        InspectAutomationAnchorRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a fresh field-selected observation of one published diagnostic target.</summary>
    [RpcMethod(2)]
    ValueTask<AcquireAutomationAnchorResponse> GetTargetSnapshotAsync(
        GetAutomationTargetSnapshotRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Builds selected diagnostic targets and writes their model-facing text from the Host process.</summary>
    [RpcMethod(3)]
    ValueTask<WriteAutomationVisualTreeFileResponse> WriteVisualTreeFileAsync(
        WriteAutomationVisualTreeFileRequest request,
        CancellationToken cancellationToken = default);
}