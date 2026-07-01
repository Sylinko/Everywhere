using System.Windows.Input;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Input;

namespace Everywhere.Common.Notification;

public sealed record DynamicNotification(
    string Id,
    IDynamicLocaleKey ContentKey,
    NotificationType Type,
    IRelayCommand<DynamicNotification>? DismissCommand,
    IDynamicLocaleKey? ActionButtonContentKey = null,
    ICommand? ActionCommand = null,
    string? Category = null
)
{
    public bool CanDismiss => DismissCommand is not null && DismissCommand.CanExecute(this);
}