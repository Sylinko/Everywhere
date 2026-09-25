using System.ComponentModel;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.Collections;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Messages;
using Everywhere.Utilities;

namespace Everywhere.Chat;

/// <summary>
/// Adapts one active assistant's availability observation to the notification presentation used by
/// ChatWindow. This object is UI-thread-affine; catalog callbacks are marshalled at its boundary.
/// </summary>
internal sealed class ChatModelAvailabilityPresenter(AssistantCatalog catalog, IKeyValueStorage keyValueStorage) : IDisposable
{
    public IReadOnlyBindableList<DynamicNotification> Notifications => _notifications.Notifications;

    private readonly DynamicNotificationManager _notifications = new(keyValueStorage, "ChatWindow");
    private CustomAssistant? _assistant;
    private AssistantConfiguration? _configuration;
    private ModelAvailabilityObservation? _observation;
    private IDisposable? _dateUpdate;
    private string? _notificationId;
    private bool _isActive;
    private bool _isDisposed;

    public void SetAssistant(CustomAssistant? assistant)
    {
        if (_isDisposed || ReferenceEquals(_assistant, assistant)) return;

        UnbindAssistant();
        _assistant = assistant;
        if (_isActive) BindAssistant();
    }

    public void SetActive(bool value)
    {
        if (_isDisposed || _isActive == value) return;

        _isActive = value;
        if (value)
        {
            BindAssistant();
            ScheduleDateUpdate();
        }
        else
        {
            DisposeHelper.DisposeToDefault(ref _dateUpdate);
            UnbindAssistant();
        }
    }

    private void BindAssistant()
    {
        if (_assistant is not { } assistant) return;

        assistant.PropertyChanged += HandleAssistantChanged;
        BindConfiguration(assistant.Configuration);
    }

    private void UnbindAssistant()
    {
        if (_assistant is not null) _assistant.PropertyChanged -= HandleAssistantChanged;
        if (_observation is not null)
        {
            _observation.PropertyChanged -= HandleAvailabilityChanged;
            _observation.Dispose();
            _observation = null;
        }
        _configuration = null;

        ClearNotification();
    }

    private void BindConfiguration(AssistantConfiguration configuration)
    {
        if (_observation is not null)
        {
            _observation.PropertyChanged -= HandleAvailabilityChanged;
            _observation.Dispose();
        }

        _configuration = configuration;
        _observation = catalog.ObserveAvailability(configuration);
        _observation.PropertyChanged += HandleAvailabilityChanged;
        PublishCurrentState();
    }

    private void HandleAssistantChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Assistant.Configuration)) return;
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isActive && ReferenceEquals(sender, _assistant) && _assistant is { } assistant)
                BindConfiguration(assistant.Configuration);
        });
    }

    private void HandleAvailabilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModelAvailabilityObservation.Value)) return;
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isActive && ReferenceEquals(sender, _observation)) PublishCurrentState();
        });
    }

    private void ScheduleDateUpdate()
    {
        DisposeHelper.DisposeToDefault(ref _dateUpdate);
        var now = DateTime.Now;
        var delay = now.Date.AddDays(1) - now + TimeSpan.FromSeconds(1);
        _dateUpdate = DispatcherTimer.RunOnce(HandleDateChanged, delay);
    }

    private void HandleDateChanged()
    {
        _dateUpdate = null;
        if (!_isActive) return;

        _observation?.Update();
        // A dismissal key contains the local date, so presentation must be reconsidered even when
        // the availability value itself did not change at midnight.
        PublishCurrentState();
        ScheduleDateUpdate();
    }

    private void PublishCurrentState()
    {
        if (_assistant is not { } assistant ||
            _configuration is not { } configuration ||
            _observation?.Value is not { ShouldShowChatNotification: true } availability)
        {
            ClearNotification();
            return;
        }

        string? providerId;
        lock (configuration)
        {
            providerId = (configuration as PresetAssistantConfiguration)?.ProviderId;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var nextId = $"{providerId ?? "official"}|{availability.CreateDismissalKey(assistant.Id, today)}";
        if (nextId == _notificationId) return;

        ClearNotification();
        _notificationId = nextId;
        var hasSettingsAction = availability.Kind != ModelAvailabilityKind.SignInRequired;
        _notifications.Push(
            new DynamicNotificationDescriptor(
                nextId,
                CreateMessageKey(availability),
                availability.Kind is ModelAvailabilityKind.Deprecated or ModelAvailabilityKind.Unavailable ?
                    NotificationType.Error :
                    NotificationType.Warning,
                ActionButtonContentKey: hasSettingsAction ?
                    new DynamicLocaleKey(LocaleKey.ChatWindow_ModelWarning_OpenAssistantSettings) :
                    null,
                ActionCommand: hasSettingsAction ? new RelayCommand(() => OpenAssistantSettings(assistant.Id)) : null));
    }

    private void ClearNotification()
    {
        _notificationId = null;
        _notifications.Clear();
    }

    private static DynamicLocaleKey CreateMessageKey(ModelAvailability availability)
    {
        var date = new DirectLocaleKey(availability.DeprecationDate?.ToString("D") ?? string.Empty);
        return availability.Kind switch
        {
            ModelAvailabilityKind.SignInRequired => new DynamicLocaleKey(LocaleKey.HandledSystemException_UserNotLogin),
            ModelAvailabilityKind.Unavailable => new DynamicLocaleKey(LocaleKey.ChatWindow_ModelWarning_Unavailable),
            ModelAvailabilityKind.Deprecated => new FormattedDynamicLocaleKey(LocaleKey.ChatWindow_ModelWarning_Deprecated, date),
            ModelAvailabilityKind.DeprecatingSoon => new FormattedDynamicLocaleKey(LocaleKey.ChatWindow_ModelWarning_DeprecatingSoon, date),
            _ => DirectLocaleKey.Empty
        };
    }

    private static void OpenAssistantSettings(Guid assistantId) =>
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(
            new ShowWindowMessage(ShowWindowMessage.MainWindow, MainViewNavigateMessage.ToCustomAssistant(assistantId)));

    public void Dispose()
    {
        if (_isDisposed) return;
        SetActive(false);
        _isDisposed = true;
        _notifications.Dispose();
    }
}