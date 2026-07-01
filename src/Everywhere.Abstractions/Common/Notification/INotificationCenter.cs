using Everywhere.Collections;

namespace Everywhere.Common.Notification;

/// <summary>
/// Represents a global notification center that holds a list of dynamic notifications.
/// </summary>
public interface INotificationCenter
{
    IReadOnlyBindableList<DynamicNotification> Notifications { get; }

    /// <summary>
    /// Gets the notification publisher for the specified type, which can be used to push, dismiss, reset, and clear notifications of that type.
    /// </summary>
    /// <returns></returns>
    INotificationPublisher GetPublisher(string categoryName);
}