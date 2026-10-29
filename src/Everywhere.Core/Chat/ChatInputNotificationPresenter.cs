using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Collections;
using Everywhere.Common.Notification;
using Everywhere.Common;

namespace Everywhere.Chat;

/// <summary>
/// Combines model notices with a dismissible notice for the currently viewed temporary chat.
/// Dismissal lasts for this visit, not for the database row or future application sessions.
/// </summary>
public sealed class ChatInputNotificationPresenter : IDisposable
{
    /// <summary>
    /// Gets the stable notices displayed above the chat input area.
    /// </summary>
    public ObservableCollection<DynamicNotification> Notifications { get; } = [];

    private readonly IReadOnlyBindableList<DynamicNotification> _modelNotifications;
    private readonly ICommand _persistCommand;
    private readonly IDisposable _currentSubscription;
    private ChatMetadata? _metadata;
    private DynamicNotification? _temporaryNotification;
    private bool _isDisposed;

    /// <summary>
    /// Observes the active conversation and existing model notices on the UI thread.
    /// </summary>
    public ChatInputNotificationPresenter(
        IChatContextManager manager,
        IReadOnlyBindableList<DynamicNotification> modelNotifications,
        ICommand persistCommand)
    {
        _modelNotifications = modelNotifications;
        _persistCommand = persistCommand;
        _modelNotifications.CollectionChanged += HandleModelNotificationsChanged;
        _currentSubscription = manager.WhenValueChanged(value => value.Current)
            .ObserveOnAvaloniaDispatcher()
            .Subscribe(context => SetCurrent(context?.Data.Metadata));
    }

    private void SetCurrent(ChatMetadata? metadata)
    {
        if (_isDisposed || ReferenceEquals(metadata, _metadata))
        {
            return;
        }

        if (_metadata is not null)
        {
            _metadata.PropertyChanged -= HandleMetadataChanged;
        }

        _metadata = metadata;
        if (metadata is not null)
        {
            metadata.PropertyChanged += HandleMetadataChanged;
        }

        UpdateTemporaryNotification();
    }

    private void HandleMetadataChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMetadata.IsTemporary))
        {
            Dispatcher.UIThread.PostOnDemand(UpdateTemporaryNotification);
        }
    }

    private void UpdateTemporaryNotification()
    {
        if (_isDisposed)
        {
            return;
        }

        _temporaryNotification = _metadata is { IsTemporary: true } metadata ?
            new DynamicNotification(
                "temporary-chat",
                new DynamicLocaleKey(LocaleKey.ChatHistory_TemporaryNotice),
                NotificationType.Information,
                new RelayCommand<DynamicNotification>(_ =>
                {
                    _temporaryNotification = null;
                    Refresh();
                }),
                new DynamicLocaleKey(LocaleKey.ChatHistory_Persist),
                new RelayCommand(() =>
                {
                    if (_persistCommand.CanExecute(metadata))
                    {
                        _persistCommand.Execute(metadata);
                    }
                })) :
            null;
        Refresh();
    }

    private void HandleModelNotificationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Refresh();
    }

    private void Refresh()
    {
        var desired = new List<DynamicNotification>(_modelNotifications);
        if (_temporaryNotification is not null)
        {
            desired.Add(_temporaryNotification);
        }

        // Keep existing notice objects and controls when only the other source changes.
        for (var index = Notifications.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(Notifications[index]))
            {
                Notifications.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var existing = Notifications.IndexOf(desired[index]);
            if (existing < 0)
            {
                Notifications.Insert(index, desired[index]);
            }
            else if (existing != index)
            {
                Notifications.Move(existing, index);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _isDisposed = true;
        _currentSubscription.Dispose();
        _modelNotifications.CollectionChanged -= HandleModelNotificationsChanged;
        if (_metadata is not null)
        {
            _metadata.PropertyChanged -= HandleMetadataChanged;
        }
    }
}