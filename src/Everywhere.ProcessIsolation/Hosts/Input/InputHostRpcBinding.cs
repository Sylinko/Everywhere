using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Input;

/// <summary>Hand-written server binder for <see cref="IInputHostRpc"/>.</summary>
public static class InputHostRpcBinding
{
    /// <summary>Registers the desired-state handler before the Host connection starts.</summary>
    public static void Bind(RpcConnection connection, IInputHostRpc implementation)
    {
        connection.RegisterRequestHandler<ApplyInputStateRequest, ApplyInputStateResponse>(
            InputHostRpcOperations.ApplyState,
            implementation.ApplyStateAsync);
    }
}

/// <summary>Hand-written Main-side binder for <see cref="IInputHostNotificationRpc"/>.</summary>
public static class InputHostNotificationRpcBinding
{
    /// <summary>
    /// Registers every notification handler before Main sends its first state
    /// snapshot, so Input Host cannot publish an event to an unbound operation.
    /// </summary>
    public static void Bind(RpcConnection connection, IInputHostNotificationRpc implementation)
    {
        connection.RegisterNotificationHandler<ShortcutTriggeredNotification>(
            InputHostNotificationRpcOperations.ShortcutTriggered,
            implementation.ShortcutTriggeredAsync);
        connection.RegisterNotificationHandler<ShortcutCaptureChangedNotification>(
            InputHostNotificationRpcOperations.CaptureChanged,
            implementation.CaptureChangedAsync);
        connection.RegisterNotificationHandler<ShortcutCaptureFinishedNotification>(
            InputHostNotificationRpcOperations.CaptureFinished,
            implementation.CaptureFinishedAsync);
    }
}