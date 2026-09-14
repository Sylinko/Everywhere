using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Coarse-grained visual Context operations owned by the Automation Host.</summary>
[RpcContract(0x0200)]
public interface IAutomationHostRpc
{
    /// <summary>Creates one connection-owned visual Context and returns its Host-assigned identity.</summary>
    [RpcMethod(1)]
    ValueTask<Guid> CreateContextAsync(CreateAutomationContextRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ensures that a current Agent turn exists without advancing an existing turn.</summary>
    [RpcMethod(2)]
    ValueTask<RpcAck> EnsureTurnAsync(AutomationContextRequest request, CancellationToken cancellationToken = default);

    /// <summary>Completes the previous Agent turn and begins a new one.</summary>
    [RpcMethod(3)]
    ValueTask<RpcAck> AdvanceTurnAsync(AutomationContextRequest request, CancellationToken cancellationToken = default);

    /// <summary>Acquires a platform-default root and builds final model-facing visual text.</summary>
    [RpcMethod(4)]
    ValueTask<AutomationVisualQueryResponse> BuildDefaultAsync(BuildDefaultVisualContextRequest request, CancellationToken cancellationToken = default);

    /// <summary>Queries one retained Agent target and returns final model-facing visual text.</summary>
    [RpcMethod(5)]
    ValueTask<AutomationVisualQueryResponse> QueryTargetAsync(QueryAutomationTargetRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads one bounded page from a retained Agent target's live text.</summary>
    [RpcMethod(6)]
    ValueTask<ReadAutomationTextResponse> ReadTextAsync(ReadAutomationTextRequest request, CancellationToken cancellationToken = default);

    /// <summary>Acquires one pre-publication visual anchor owned by its parent Context.</summary>
    [RpcMethod(7)]
    ValueTask<AcquireAutomationAnchorResponse> AcquireAnchorAsync(AcquireAutomationAnchorRequest request, CancellationToken cancellationToken = default);

    /// <summary>Builds final model-facing text from pre-publication visual anchors.</summary>
    [RpcMethod(8)]
    ValueTask<AutomationVisualQueryResponse> BuildAnchorsAsync(BuildAutomationAnchorsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Streams one captured anchor or Agent target as raw, Skia-compatible pixels.</summary>
    [RpcMethod(9)]
    IAsyncEnumerable<AutomationCaptureFrame> CaptureAsync(CaptureAutomationVisualRequest request, CancellationToken cancellationToken = default);

    /// <summary>Executes an already-authorized ordered action batch against retained Agent targets.</summary>
    [RpcMethod(10)]
    ValueTask<RpcAck> ExecuteActionsAsync(ExecuteAutomationActionsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lists current top-level windows and publishes their Agent target IDs.</summary>
    [RpcMethod(11)]
    ValueTask<AutomationVisualQueryResponse> ListWindowsAsync(ListAutomationWindowsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns a fresh field-selected scalar observation of one Context-owned visual anchor.</summary>
    [RpcMethod(12)]
    ValueTask<AcquireAutomationAnchorResponse> GetElementSnapshotAsync(GetAutomationElementSnapshotRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates one Context-owned interactive visual picker using a caller-allocated resource ID.</summary>
    [RpcMethod(13)]
    ValueTask<RpcAck> BeginPickerAsync(BeginVisualPickerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resolves one revisioned picker update inside the Automation Host.</summary>
    [RpcMethod(14)]
    ValueTask<VisualPickerObservation> UpdatePickerAsync(UpdateVisualPickerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Transfers the exact confirmed picker candidate into a caller-owned visual anchor.</summary>
    [RpcMethod(15)]
    ValueTask<AcquireAutomationAnchorResponse> ConfirmPickerAsync(ConfirmVisualPickerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Moves one pre-publication anchor into another Context on this connection.</summary>
    [RpcMethod(16)]
    ValueTask<AcquireAutomationAnchorResponse> MoveAnchorAsync(MoveAutomationAnchorRequest request, CancellationToken cancellationToken = default);
}