using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using DynamicData.Binding;
using Everywhere.Collections;
using Everywhere.Configuration;

namespace Everywhere.Common.Notification;

public sealed class NotificationCenter : INotificationCenter, IDisposable
{
    private static readonly IComparer<NotificationEntry> NotificationComparer =
        SortExpressionComparer<NotificationEntry>
            .Ascending(static entry => GetNotificationTypePriority(entry.Notification.Type))
            .ThenByDescending(static entry => entry.SortSequence);

    public IReadOnlyBindableList<DynamicNotification> Notifications { get; }

    private readonly IKeyValueStorage _keyValueStorage;
    private readonly SourceCache<NotificationEntry, string> _notificationsSource = new(static entry => entry.StorageKey);
    private readonly ObservableCollection<DynamicNotification> _notifications = [];
    private readonly ReadOnlyObservableCollection<NotificationEntry> _notificationEntries;
    private readonly IDisposable _notificationsViewDisposable;
    private readonly Lock _gate = new();
    private long _nextSortSequence;

    public NotificationCenter(IKeyValueStorage keyValueStorage)
    {
        _keyValueStorage = keyValueStorage;

        _notificationsViewDisposable = _notificationsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .SortAndBind(out _notificationEntries, NotificationComparer)
            .Subscribe();
        ((INotifyCollectionChanged)_notificationEntries).CollectionChanged += HandleNotificationEntriesChanged;
        Notifications = _notifications.ToReadOnlyBindableList();
    }

    public INotificationPublisher GetPublisher(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return new NotificationPublisher(this, categoryName);
    }

    private void Push(string categoryName, DynamicNotificationDescriptor dynamicNotification)
    {
        ValidateCategoryName(categoryName);
        ValidateDescriptor(dynamicNotification);

        var storageKey = CreateStorageKey(dynamicNotification.Id, categoryName);
        if (dynamicNotification is { CanDismiss: true, ForceShow: false } && _keyValueStorage.Contains(storageKey)) return;

        lock (_gate)
        {
            var sequence = _nextSortSequence++;
            _notificationsSource.AddOrUpdate(CreateEntry(categoryName, dynamicNotification, sequence));
        }
    }

    private void Dismiss(string categoryName, string id)
    {
        ValidateCategoryName(categoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        lock (_gate)
        {
            _notificationsSource.RemoveKey(CreateStorageKey(id, categoryName));
        }
    }

    private void Clear(string categoryName)
    {
        ValidateCategoryName(categoryName);

        lock (_gate)
        {
            RemoveCategoryNotifications(categoryName);
        }
    }

    private void Reset(string categoryName, IEnumerable<DynamicNotificationDescriptor> notifications)
    {
        ValidateCategoryName(categoryName);
        ArgumentNullException.ThrowIfNull(notifications);

        var descriptors = notifications.ToList();
        foreach (var notification in descriptors)
        {
            ValidateDescriptor(notification);
        }

        lock (_gate)
        {
            RemoveCategoryNotifications(categoryName);

            var visibleDescriptors = descriptors
                .Where(notification => notification is not { CanDismiss: true, ForceShow: false } ||
                    !_keyValueStorage.Contains(CreateStorageKey(notification.Id, categoryName)))
                .ToList();

            var batchStart = _nextSortSequence;
            _nextSortSequence += visibleDescriptors.Count;

            for (var index = 0; index < visibleDescriptors.Count; index++)
            {
                var sequence = batchStart + visibleDescriptors.Count - index - 1;
                _notificationsSource.AddOrUpdate(CreateEntry(categoryName, visibleDescriptors[index], sequence));
            }
        }
    }

    private void DismissNotification(DynamicNotification? notification)
    {
        if (notification is not { CanDismiss: true, Category: { } categoryName }) return;

        var storageKey = CreateStorageKey(notification.Id, categoryName);
        _keyValueStorage.Set(storageKey, true);

        lock (_gate)
        {
            _notificationsSource.RemoveKey(storageKey);
        }
    }

    private void RemoveCategoryNotifications(string categoryName)
    {
        var keys = _notificationsSource.Items
            .Where(entry => entry.CategoryName == categoryName)
            .Select(static entry => entry.StorageKey)
            .ToArray();

        _notificationsSource.Edit(updater => updater.RemoveKeys(keys));
    }

    private NotificationEntry CreateEntry(
        string categoryName,
        DynamicNotificationDescriptor notification,
        long sortSequence)
    {
        var storageKey = CreateStorageKey(notification.Id, categoryName);
        return new NotificationEntry(
            storageKey,
            categoryName,
            CreateNotification(categoryName, notification),
            sortSequence);
    }

    private DynamicNotification CreateNotification(string categoryName, DynamicNotificationDescriptor notification) => new(
        notification.Id,
        notification.ContentKey,
        notification.Type,
        notification.CanDismiss ? new RelayCommand<DynamicNotification>(DismissNotification) : null,
        notification.ActionButtonContentKey,
        notification.ActionCommand,
        categoryName);

    private static string CreateStorageKey(string id, string categoryName) =>
        $"NotificationCenter:{EscapeStorageSegment(categoryName)}:{EscapeStorageSegment(id)}";

    private static string EscapeStorageSegment(string value) => Uri.EscapeDataString(value);

    private static void ValidateCategoryName(string categoryName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);

    private static void ValidateDescriptor(DynamicNotificationDescriptor notification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Id);
        ArgumentNullException.ThrowIfNull(notification.ContentKey);
    }

    private static int GetNotificationTypePriority(NotificationType type) => type switch
    {
        NotificationType.Error => 0,
        NotificationType.Warning => 1,
        NotificationType.Success => 2,
        _ => 3
    };

    public void Dispose()
    {
        ((INotifyCollectionChanged)_notificationEntries).CollectionChanged -= HandleNotificationEntriesChanged;
        _notificationsViewDisposable.Dispose();
        _notificationsSource.Dispose();
    }

    private void HandleNotificationEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
            {
                ArgumentNullException.ThrowIfNull(e.NewItems);
                var insertIndex = e.NewStartingIndex;
                foreach (var entry in e.NewItems.Cast<NotificationEntry>())
                {
                    _notifications.Insert(insertIndex++, entry.Notification);
                }

                break;
            }
            case NotifyCollectionChangedAction.Remove:
            {
                ArgumentNullException.ThrowIfNull(e.OldItems);
                for (var index = 0; index < e.OldItems.Count; index++)
                {
                    _notifications.RemoveAt(e.OldStartingIndex);
                }

                break;
            }
            case NotifyCollectionChangedAction.Replace:
            {
                ArgumentNullException.ThrowIfNull(e.NewItems);
                var replaceIndex = e.NewStartingIndex;
                foreach (var entry in e.NewItems.Cast<NotificationEntry>())
                {
                    _notifications[replaceIndex++] = entry.Notification;
                }

                break;
            }
            case NotifyCollectionChangedAction.Move:
            {
                if (e.OldItems is not { Count: 1 })
                {
                    ResetNotifications();
                    break;
                }

                _notifications.Move(e.OldStartingIndex, e.NewStartingIndex);
                break;
            }
            case NotifyCollectionChangedAction.Reset:
            {
                ResetNotifications();
                break;
            }
            default:
            {
                throw new ArgumentOutOfRangeException(nameof(e.Action), e.Action, null);
            }
        }
    }

    private void ResetNotifications()
    {
        _notifications.Clear();
        foreach (var entry in _notificationEntries)
        {
            _notifications.Add(entry.Notification);
        }
    }

    private sealed record NotificationEntry(
        string StorageKey,
        string CategoryName,
        DynamicNotification Notification,
        long SortSequence);

    private sealed class NotificationPublisher(NotificationCenter notificationCenter, string categoryName) : INotificationPublisher
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(
            string id,
            IDynamicResourceKey contentKey,
            NotificationType type = NotificationType.Information,
            bool canDismiss = true,
            bool forceShow = false,
            IDynamicResourceKey? actionButtonContentKey = null,
            ICommand? actionCommand = null)
        {
            notificationCenter.Push(
                categoryName,
                new DynamicNotificationDescriptor(
                    id,
                    contentKey,
                    type,
                    canDismiss,
                    forceShow,
                    actionButtonContentKey,
                    actionCommand));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dismiss(string id) => notificationCenter.Dismiss(categoryName, id);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset(params IEnumerable<DynamicNotificationDescriptor> notifications) =>
            notificationCenter.Reset(categoryName, notifications);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear() => notificationCenter.Clear(categoryName);
    }
}