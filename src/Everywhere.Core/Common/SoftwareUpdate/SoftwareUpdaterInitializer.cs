using System.ComponentModel;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Messages;
using Everywhere.Utilities;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.Common;

/// <summary>
/// Initializes the software updater by subscribing to settings changes and starting the automatic update check.
/// </summary>
/// <param name="softwareUpdater"></param>
/// <param name="settings"></param>
/// <param name="notificationPublisher"></param>
public sealed class SoftwareUpdaterInitializer(
    ISoftwareUpdater softwareUpdater,
    Settings settings,
    PersistentState persistentState,
    INotificationPublisher<SoftwareUpdater> notificationPublisher,
    IServiceProvider serviceProvider
) : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    private const string UpdateNotificationId = "software_update";
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(4);

    private readonly ReusableCancellationTokenSource _cancellationTokenSource = new();

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settings.Common.PropertyChanged += HandleCommonPropertyChanged;
        softwareUpdater.PropertyChanged += HandleSoftwareUpdaterPropertyChanged;

#if !DEBUG
        // Release builds recover prepared packages even when automatic checking is disabled.
        // Debug startup stays quiet; explicit update actions can still recover the cache.
        softwareUpdater
            .RecoverCachedPackageAsync(cancellationToken)
            .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
#endif

        PublishUpdateNotification();

        if (settings.Common.IsAutomaticUpdateCheckEnabled)
        {
            softwareUpdater.RunAutomaticCheckInBackground(AutomaticCheckInterval, _cancellationTokenSource.Token);
        }

        return Task.CompletedTask;
    }

    private void HandleCommonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommonSettings.IsAutomaticUpdateCheckEnabled)) return;

        if (settings.Common.IsAutomaticUpdateCheckEnabled)
        {
            softwareUpdater.RunAutomaticCheckInBackground(AutomaticCheckInterval, _cancellationTokenSource.Token);
        }
        else
        {
            _cancellationTokenSource.Cancel();
        }
    }

    private void HandleSoftwareUpdaterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ISoftwareUpdater.DisplayUpdate))
        {
            PublishUpdateNotification();
        }

        if (e.PropertyName != nameof(ISoftwareUpdater.LastCheckTime)) return;

        var updater = sender.NotNull<ISoftwareUpdater>();
        if (updater.LastCheckTime.HasValue)
        {
            persistentState.LastUpdateCheckTime = updater.LastCheckTime;
        }
    }

    private void PublishUpdateNotification()
    {
        if (softwareUpdater.DisplayUpdate is not { } displayedUpdate)
        {
            notificationPublisher.Clear();
            return;
        }

        var readyUpdate = softwareUpdater.ReadyUpdate;
        var isReady = softwareUpdater.IsReadyUpdateConfirmedLatest && readyUpdate?.HasSamePackageIdentity(displayedUpdate) == true;

        notificationPublisher.Push(
            UpdateNotificationId,
            new FormattedDynamicLocaleKey(
                isReady ?
                    LocaleKey.HomeNotification_UpdateReady :
                    LocaleKey.HomeNotification_UpdateAvailable,
                new DirectLocaleKey(displayedUpdate.Version.ToString())),
            NotificationType.Information,
            canDismiss: true,
            forceShow: true,
            isReady ?
                new DynamicLocaleKey(LocaleKey.HomeNotification_InstallUpdate) :
                new DynamicLocaleKey(LocaleKey.HomeNotification_ViewUpdate),
            isReady ?
                new AsyncRelayCommand(() => InstallUpdateAsync(readyUpdate.NotNull())) :
                new RelayCommand(OpenChangeLog));
    }

    private async Task InstallUpdateAsync(SoftwareUpdateMetadata target)
    {
        if (softwareUpdater.CurrentOperation is { IsActive: true }) return;

        try
        {
            await softwareUpdater.PerformUpdateAsync(target);
        }
        catch (Exception ex)
        {
            ToastManager.Error(LocaleKey.Common_Error.I18N(), ex.GetFriendlyMessage());
        }
    }

    private void OpenChangeLog()
    {
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(
            new ShowWindowMessage(ShowWindowMessage.MainWindow, serviceProvider.GetRequiredService<ChangeLogView>()));
    }
}