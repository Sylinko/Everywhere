using System.Windows.Input;
using Avalonia.Controls.Notifications;

namespace Everywhere.Common.Notification;

public interface INotificationPublisher
{
    void Push(
        string id,
        IDynamicResourceKey contentKey,
        NotificationType type = NotificationType.Information,
        bool canDismiss = true,
        bool forceShow = false,
        IDynamicResourceKey? actionButtonContentKey = null,
        ICommand? actionCommand = null);

    void Dismiss(string id);

    void Reset(params IEnumerable<DynamicNotificationDescriptor> notifications);

    void Clear();
}

// ReSharper disable once UnusedTypeParameter
public interface INotificationPublisher<out TCategory> : INotificationPublisher;