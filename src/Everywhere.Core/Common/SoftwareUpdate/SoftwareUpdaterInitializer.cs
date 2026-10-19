using System.ComponentModel;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Messages;
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
) : IAsyncInitializer, IDisposable
{
    private const string UpdateNotificationId = "software_update";
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(4);

    // Setup and settings callbacks run on the UI thread. The application drains startup and
    // exits the desktop loop before DI disposal, so teardown needs neither a lock nor dispatch.
    private CancellationToken _applicationToken;
    private CancellationTokenSource? _automaticCheckSource;

    #region IAsyncInitializer

    /// <inheritdoc />
    static AsyncInitializerIndex IAsyncInitializer.Index => AsyncInitializerIndex.Startup;

    /// <inheritdoc />
    Task IAsyncInitializer.InitializeAsync(CancellationToken cancellationToken)
    {
        return Dispatcher.UIThread.InvokeAsync(
            () => Initialize(cancellationToken),
            DispatcherPriority.Normal,
            cancellationToken).GetTask();
    }

    private void Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _applicationToken = cancellationToken;
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
        UpdateAutomaticChecking();
    }

    #endregion

    /// <summary>
    /// Detaches settings/update notifications and cancels the automatic update schedule.
    /// </summary>
    public void Dispose()
    {
        settings.Common.PropertyChanged -= HandleCommonPropertyChanged;
        softwareUpdater.PropertyChanged -= HandleSoftwareUpdaterPropertyChanged;
        StopAutomaticChecking();
    }

    private void HandleCommonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommonSettings.IsAutomaticUpdateCheckEnabled)) return;

        Dispatcher.UIThread.PostOnDemand(UpdateAutomaticChecking);
    }

    private void UpdateAutomaticChecking()
    {
        if (_applicationToken.IsCancellationRequested) return;

        StopAutomaticChecking();
        if (!settings.Common.IsAutomaticUpdateCheckEnabled) return;

        _automaticCheckSource = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
        softwareUpdater.RunAutomaticCheckInBackground(AutomaticCheckInterval, _automaticCheckSource.Token);
    }

    // Only this automatic run is canceled; manual work belongs to the updater.
    private void StopAutomaticChecking()
    {
        _automaticCheckSource?.Cancel();
        _automaticCheckSource?.Dispose();
        _automaticCheckSource = null;
    }

    private void HandleSoftwareUpdaterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SoftwareUpdater publishes property changes on the UI thread.
        if (_applicationToken.IsCancellationRequested) return;

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
            await softwareUpdater.PerformUpdateAsync(target, CancellationToken.None);
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