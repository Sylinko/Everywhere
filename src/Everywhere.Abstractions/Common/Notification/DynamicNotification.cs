using System.Windows.Input;
using Avalonia.Controls.Notifications;

namespace Everywhere.Common.Notification;

public sealed record DynamicNotification(
    string Id,
    IDynamicResourceKey ContentKey,
    NotificationType Type,
    ICommand? DismissCommand,
    IDynamicResourceKey? ActionButtonContentKey = null,
    ICommand? ActionCommand = null,
    string? Category = null
)
{
    public bool CanDismiss => DismissCommand is not null && DismissCommand.CanExecute(this);
}
