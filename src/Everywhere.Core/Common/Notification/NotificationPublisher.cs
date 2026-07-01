using System.Windows.Input;
using Avalonia.Controls.Notifications;

namespace Everywhere.Common.Notification;

/// <summary>
/// Represents a notification publisher for a specific category, which can be used to push, dismiss, reset, and clear notifications of that category.
/// </summary>
/// <param name="notificationCenter"></param>
/// <typeparam name="TCategory"></typeparam>
public sealed class NotificationPublisher<TCategory>(INotificationCenter notificationCenter) : INotificationPublisher<TCategory>
{
    private readonly INotificationPublisher _publisher = notificationCenter.GetPublisher(typeof(TCategory).FullName ?? typeof(TCategory).Name);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(
        string id,
        IDynamicLocaleKey contentKey,
        NotificationType type = NotificationType.Information,
        bool canDismiss = true,
        bool forceShow = false,
        IDynamicLocaleKey? actionButtonContentKey = null,
        ICommand? actionCommand = null)
    {
        _publisher.Push(id, contentKey, type, canDismiss, forceShow, actionButtonContentKey, actionCommand);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dismiss(string id)
    {
        _publisher.Dismiss(id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(params IEnumerable<DynamicNotificationDescriptor> notifications)
    {
        _publisher.Reset(notifications);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        _publisher.Clear();
    }
}