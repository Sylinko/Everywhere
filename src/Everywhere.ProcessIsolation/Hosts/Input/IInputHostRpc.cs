using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Input;

/// <summary>
/// Main-to-Input-Host desired-state contract. The complete snapshot is the sole
/// source of connection-owned registrations and capture state.
/// </summary>
[RpcContract(InputHostRpcOperations.ContractId)]
public interface IInputHostRpc
{
    /// <summary>Atomically replaces all desired Input state for the current connection.</summary>
    [RpcMethod(InputHostRpcOperations.ApplyStateMethodId)]
    ValueTask<ApplyInputStateResponse> ApplyStateAsync(
        ApplyInputStateRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Input-Host-to-Main event contract. These methods are one-way notifications;
/// completion only means the sender's bounded RPC queue accepted the frame.
/// </summary>
[RpcContract(InputHostNotificationRpcOperations.ContractId)]
public interface IInputHostNotificationRpc
{
    /// <summary>Reports one activated keyboard or mouse registration.</summary>
    [RpcNotification(InputHostNotificationRpcOperations.ShortcutTriggeredMethodId)]
    ValueTask ShortcutTriggeredAsync(
        ShortcutTriggeredNotification notification,
        CancellationToken cancellationToken = default);

    /// <summary>Reports the latest in-progress keyboard capture value.</summary>
    [RpcNotification(InputHostNotificationRpcOperations.CaptureChangedMethodId)]
    ValueTask CaptureChangedAsync(
        ShortcutCaptureChangedNotification notification,
        CancellationToken cancellationToken = default);

    /// <summary>Reports the terminal value of the active keyboard capture.</summary>
    [RpcNotification(InputHostNotificationRpcOperations.CaptureFinishedMethodId)]
    ValueTask CaptureFinishedAsync(
        ShortcutCaptureFinishedNotification notification,
        CancellationToken cancellationToken = default);
}