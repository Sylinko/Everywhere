using Everywhere.Configuration;

namespace Everywhere.Common.Notification;

/// <summary>
/// Owns inline notices and cross-window announcements. Each presentation keeps its own lifecycle;
/// acknowledgment storage and the application-wide service identity are shared.
/// </summary>
public sealed partial class NotificationCenter : INotificationCenter, IDisposable
{
    private readonly IKeyValueStorage _keyValueStorage;

    /// <summary>
    /// Creates the application notification center.
    /// </summary>
    public NotificationCenter(IKeyValueStorage keyValueStorage)
    {
        _keyValueStorage = keyValueStorage;
        InitializeInlineNotifications();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposePopups();
        DisposeInlineNotifications();
    }
}