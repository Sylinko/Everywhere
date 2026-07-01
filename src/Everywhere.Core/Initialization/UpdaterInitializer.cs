using System.ComponentModel;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Messages;
using Everywhere.Utilities;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Initialization;

/// <summary>
/// Initializes the software updater by subscribing to settings changes and starting the automatic update check.
/// </summary>
/// <param name="softwareUpdater"></param>
/// <param name="settings"></param>
/// <param name="notificationPublisher"></param>
public sealed class UpdaterInitializer(
    ISoftwareUpdater softwareUpdater,
    Settings settings,
    INotificationPublisher<SoftwareUpdater> notificationPublisher,
    IServiceProvider serviceProvider
) : IAsyncInitializer
{
    private const string UpdateNotificationId = "software_update";

    private readonly ReusableCancellationTokenSource _cancellationTokenSource = new();
    private SoftwareUpdateMetadata? _observedLatestUpdate;

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        settings.Common.PropertyChanged += HandleCommonPropertyChanged;
        softwareUpdater.PropertyChanged += HandleSoftwareUpdaterPropertyChanged;
        ObserveLatestUpdate(softwareUpdater.LatestUpdate);
        PublishUpdateNotification();

        if (settings.Common.IsAutomaticUpdateCheckEnabled)
        {
            softwareUpdater.RunAutomaticCheckInBackground(TimeSpan.FromHours(12), _cancellationTokenSource.Token);
        }

        return Task.CompletedTask;
    }

    private void HandleCommonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommonSettings.IsAutomaticUpdateCheckEnabled)) return;

        if (settings.Common.IsAutomaticUpdateCheckEnabled)
        {
            softwareUpdater.RunAutomaticCheckInBackground(TimeSpan.FromHours(12), _cancellationTokenSource.Token);
        }
        else
        {
            _cancellationTokenSource.Cancel();
        }
    }

    private void HandleSoftwareUpdaterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ISoftwareUpdater.LatestUpdate))
        {
            ObserveLatestUpdate(softwareUpdater.LatestUpdate);
            PublishUpdateNotification();
        }

        if (e.PropertyName != nameof(ISoftwareUpdater.LastCheckTime)) return;

        var updater = sender.NotNull<ISoftwareUpdater>();
        if (updater.LastCheckTime.HasValue)
        {
            settings.Common.LastUpdateCheckTime = updater.LastCheckTime;
        }
    }

    private void HandleLatestUpdatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoftwareUpdateMetadata.IsReady))
        {
            PublishUpdateNotification();
        }
    }

    private void ObserveLatestUpdate(SoftwareUpdateMetadata? latestUpdate)
    {
        if (ReferenceEquals(_observedLatestUpdate, latestUpdate)) return;

        if (_observedLatestUpdate is not null)
        {
            _observedLatestUpdate.PropertyChanged -= HandleLatestUpdatePropertyChanged;
        }

        _observedLatestUpdate = latestUpdate;
        if (_observedLatestUpdate is not null)
        {
            _observedLatestUpdate.PropertyChanged += HandleLatestUpdatePropertyChanged;
        }
    }

    private void PublishUpdateNotification()
    {
        if (softwareUpdater.LatestUpdate is not { } latestUpdate)
        {
            notificationPublisher.Clear();
            return;
        }

        notificationPublisher.Push(
            UpdateNotificationId,
            new FormattedDynamicLocaleKey(
                latestUpdate.IsReady ?
                    LocaleKey.HomeNotification_UpdateReady :
                    LocaleKey.HomeNotification_UpdateAvailable,
                new DirectLocaleKey(latestUpdate.Version.ToString())),
            NotificationType.Information,
            canDismiss: true,
            forceShow: true,
            latestUpdate.IsReady ?
                new DynamicLocaleKey(LocaleKey.HomeNotification_InstallUpdate) :
                new DynamicLocaleKey(LocaleKey.HomeNotification_ViewUpdate),
            latestUpdate.IsReady ?
                new AsyncRelayCommand(() => softwareUpdater.PerformUpdateAsync()) :
                new RelayCommand(OpenChangeLog));
    }

    private void OpenChangeLog()
    {
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(
            new ShowWindowMessage(ShowWindowMessage.MainWindow, serviceProvider.GetRequiredService<ChangeLogView>()));
    }
}
