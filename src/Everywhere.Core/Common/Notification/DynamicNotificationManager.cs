using CommunityToolkit.Mvvm.Input;
using Everywhere.Collections;
using Everywhere.Configuration;
using ObservableCollections;

namespace Everywhere.Common.Notification;

public sealed class DynamicNotificationManager : IDisposable
{
    public IReadOnlyBindableList<DynamicNotification> Notifications { get; }

    private readonly IKeyValueStorage _keyValueStorage;
    private readonly string? _scope;
    private readonly ObservableCollections.ObservableDictionary<string, DynamicNotification> _notificationsSource = new();
    private readonly IDisposable _notificationsViewDisposable;

    public DynamicNotificationManager(IKeyValueStorage keyValueStorage, string? scope = null)
    {
        _keyValueStorage = keyValueStorage;
        _scope = scope;

        Notifications = _notificationsSource
            .CreateView(kv => kv.Value)
            .ToNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current)
            .ToReadOnlyBindableList(out _notificationsViewDisposable);
    }

    /// <summary>
    /// Pushes a new notification to be displayed. The notification will be automatically dismissed after a certain period, or can be dismissed manually if CanDismiss is true.
    /// If the notification with the same ID already exists, it will be replaced with the new one. This allows for updating existing notifications without stacking duplicates.
    /// Dismissed notifications will be automatically removed from the list, IKeyValueStorage can be used to persist dismissed notification IDs to prevent them from showing again in the future.
    /// </summary>
    public void Push(DynamicNotificationDescriptor dynamicNotification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dynamicNotification.Id);
        ArgumentNullException.ThrowIfNull(dynamicNotification.ContentKey);

        var key = CreateStorageKey(dynamicNotification.Id, _scope);
        if (dynamicNotification is { CanDismiss: true, ForceShow: false } && _keyValueStorage.Contains(key)) return;

        _notificationsSource[key] = CreateNotification(key, dynamicNotification);
    }

    public void Dismiss(string id) => _notificationsSource.Remove(CreateStorageKey(id, _scope));

    /// <summary>
    /// Clears all notifications from the manager.
    /// This will remove all notifications from the display, but does not affect the dismissed state of any notifications in the storage.
    /// Use with caution, as this may lead to notifications being shown again if they are not marked as dismissed in the storage.
    /// </summary>
    public void Clear()
    {
        _notificationsSource.Clear();
    }

    public void Reset(params IEnumerable<DynamicNotificationDescriptor> notifications)
    {
        _notificationsSource.Clear();
        foreach (var notification in notifications)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(notification.Id);
            ArgumentNullException.ThrowIfNull(notification.ContentKey);

            var key = CreateStorageKey(notification.Id, _scope);
            if (notification is { CanDismiss: true, ForceShow: false } && _keyValueStorage.Contains(key)) continue;

            _notificationsSource[key] = CreateNotification(key, notification);
        }
    }

    private static string CreateStorageKey(string id, string? scope) =>
        scope.IsNullOrEmpty() ? $"Notification:{id}" : $"Notification:{scope}:{id}";

    private DynamicNotification CreateNotification(string key, DynamicNotificationDescriptor notification) => new(
        key,
        notification.ContentKey,
        notification.Type,
        notification.CanDismiss ? new RelayCommand<DynamicNotification>(DismissNotification) : null,
        notification.ActionButtonContentKey,
        notification.ActionCommand);

    private void DismissNotification(DynamicNotification? notification)
    {
        if (notification is { CanDismiss: true })
        {
            // Mark the notification as dismissed in the storage to prevent it from showing again in the future.
            _keyValueStorage.Set(notification.Id, true);
            _notificationsSource.Remove(notification.Id);
        }
    }

    public void Dispose()
    {
        _notificationsViewDisposable.Dispose();
    }
}
