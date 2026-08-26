using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Input;

/// <summary>Thin typed client for Main's desired-state calls to Input Host.</summary>
public sealed class InputHostRpcClient(RpcConnection connection) : IInputHostRpc
{
    /// <inheritdoc />
    public ValueTask<ApplyInputStateResponse> ApplyStateAsync(
        ApplyInputStateRequest request,
        CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<ApplyInputStateRequest, ApplyInputStateResponse>(
            InputHostRpcOperations.ApplyState,
            request,
            cancellationToken);
}

/// <summary>Thin typed sender for Input Host's one-way notifications to Main.</summary>
public sealed class InputHostNotificationRpcClient(RpcConnection connection) : IInputHostNotificationRpc
{
    /// <inheritdoc />
    public ValueTask ShortcutTriggeredAsync(
        ShortcutTriggeredNotification notification,
        CancellationToken cancellationToken = default) =>
        connection.SendNotificationAsync(
            InputHostNotificationRpcOperations.ShortcutTriggered,
            notification,
            cancellationToken);

    /// <inheritdoc />
    public ValueTask CaptureChangedAsync(
        ShortcutCaptureChangedNotification notification,
        CancellationToken cancellationToken = default) =>
        connection.SendNotificationAsync(
            InputHostNotificationRpcOperations.CaptureChanged,
            notification,
            cancellationToken);

    /// <inheritdoc />
    public ValueTask CaptureFinishedAsync(
        ShortcutCaptureFinishedNotification notification,
        CancellationToken cancellationToken = default) =>
        connection.SendNotificationAsync(
            InputHostNotificationRpcOperations.CaptureFinished,
            notification,
            cancellationToken);
}