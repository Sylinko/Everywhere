using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Collections.Specialized;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using DynamicData.Binding;
using Everywhere.AI;
using Everywhere.AI.Configurator;
using Everywhere.Cloud;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Messages;

namespace Everywhere.Chat;

public sealed class ChatWindowNotificationService : IChatWindowNotificationService, IDisposable
{
    private const string NotificationScope = "ChatWindow.OfficialModel";

    public IReadOnlyBindableList<DynamicNotification> Notifications => _notificationManager.Notifications;

    private readonly Settings _settings;
    private readonly IOfficialModelProvider _officialModelProvider;
    private readonly DynamicNotificationManager _notificationManager;
    private readonly CompositeDisposable _disposables = new(2);

    public ChatWindowNotificationService(
        Settings settings,
        IKeyValueStorage keyValueStorage,
        IOfficialModelProvider officialModelProvider)
    {
        _settings = settings;
        _officialModelProvider = officialModelProvider;
        _notificationManager = new DynamicNotificationManager(keyValueStorage, NotificationScope).DisposeWith(_disposables);

        var selectedAssistantChanges = _settings.Model
            .WhenValueChanged(static x => x.SelectedCustomAssistant)
            .Select(static _ => 0);

        var assistantChanges = _settings.Model.CustomAssistants
            .ToObservableChangeSet()
            .AutoRefresh(static x => x.ConfiguratorType)
            .AutoRefresh(static x => x.ModelId)
            .AutoRefresh(static x => x.DeprecationDate)
            .ToCollection()
            .Select(static _ => 0);

        var officialModelDefinitionChanges = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => _officialModelProvider.ModelDefinitions.CollectionChanged += h,
                h => _officialModelProvider.ModelDefinitions.CollectionChanged -= h)
            .Select(static _ => 0);

        Observable
            .Merge(selectedAssistantChanges, assistantChanges, officialModelDefinitionChanges)
            .StartWith(0)
            .ObserveOnAvaloniaDispatcher()
            .Subscribe(_ => UpdateOfficialModelNotifications())
            .AddTo(_disposables);
    }

    private void UpdateOfficialModelNotifications()
    {
        var assistant = _settings.Model.SelectedCustomAssistant;
        if (assistant is null || assistant.ConfiguratorType != AssistantConfiguratorType.Official)
        {
            _notificationManager.Clear();
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var availability = ModelAvailability.Evaluate(
            assistant,
            _officialModelProvider.ModelDefinitions,
            today);
        if (!availability.ShouldShowChatNotification)
        {
            _notificationManager.Clear();
            return;
        }

        var dismissalKey = availability.CreateDismissalKey(assistant.Id, today);
        _notificationManager.Reset(
            new DynamicNotificationDescriptor(
                dismissalKey,
                CreateOfficialModelWarningMessageKey(availability),
                availability.Kind is ModelAvailabilityKind.Deprecated or ModelAvailabilityKind.Unavailable ?
                    NotificationType.Error :
                    NotificationType.Warning,
                ActionButtonContentKey: new DynamicLocaleKey(LocaleKey.ChatWindow_ModelWarning_OpenAssistantSettings),
                ActionCommand: new RelayCommand(() => OpenAssistantSettings(assistant.Id))));
    }

    private static IDynamicLocaleKey CreateOfficialModelWarningMessageKey(ModelAvailability availability)
    {
        var deprecationDate = new DirectLocaleKey(availability.DeprecationDate?.ToString("D") ?? string.Empty);
        return availability.Kind switch
        {
            ModelAvailabilityKind.Unavailable => new FormattedDynamicLocaleKey(
                LocaleKey.ChatWindow_ModelWarning_Unavailable),
            ModelAvailabilityKind.Deprecated => new FormattedDynamicLocaleKey(
                LocaleKey.ChatWindow_ModelWarning_Deprecated,
                deprecationDate),
            ModelAvailabilityKind.DeprecatingSoon => new FormattedDynamicLocaleKey(
                LocaleKey.ChatWindow_ModelWarning_DeprecatingSoon,
                deprecationDate),
            _ => DirectLocaleKey.Empty
        };
    }

    private static void OpenAssistantSettings(Guid assistantId)
    {
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(
            new ShowWindowMessage(ShowWindowMessage.MainWindow, "CustomAssistantPage"));
        WeakReferenceMessenger.Default.Send(new SelectCustomAssistantMessage(assistantId)); // TODO
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
