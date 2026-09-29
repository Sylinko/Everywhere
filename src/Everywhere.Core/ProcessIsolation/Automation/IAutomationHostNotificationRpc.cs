using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Automation-Host-to-Main connection notifications.</summary>
[RpcContract(0x0202)]
public interface IAutomationHostNotificationRpc
{
    /// <summary>Transfers one text-selection result and optional retained source to Main.</summary>
    [RpcMethod(1)]
    ValueTask TextSelectionObservedAsync(
        TextSelectionObservedNotification notification,
        CancellationToken cancellationToken = default);
}