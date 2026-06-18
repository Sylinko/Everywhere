using System.Windows.Input;
using Avalonia.Controls.Notifications;

namespace Everywhere.Common.Notification;

/// <summary>
/// Represents the descriptor of a dynamic notification, which can be used to create a <see cref="DynamicNotification"/>.
/// </summary>
/// <param name="Id"></param>
/// <param name="ContentKey"></param>
/// <param name="Type"></param>
/// <param name="CanDismiss"></param>
/// <param name="ForceShow"></param>
/// <param name="ActionButtonContentKey"></param>
/// <param name="ActionCommand"></param>
public readonly record struct DynamicNotificationDescriptor(
    string Id,
    IDynamicResourceKey ContentKey,
    NotificationType Type = NotificationType.Information,
    bool CanDismiss = true,
    bool ForceShow = false,
    IDynamicResourceKey? ActionButtonContentKey = null,
    ICommand? ActionCommand = null);