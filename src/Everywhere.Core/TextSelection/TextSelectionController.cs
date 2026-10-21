using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.TextSelection;

/// <summary>
/// Coordinates the two text-selection preferences and their shared native monitoring operation.
/// User commands and observable state belong to the UI thread; Host restoration enters through the dispatcher.
/// </summary>
public sealed partial class TextSelectionController : ObservableObject, IDisposable
{
    /// <summary>
    /// Gets the automatic attachment preference, independently of the toolbar.
    /// </summary>
    public bool IsAutomaticAttachmentEnabled => _settings.TextSelection.IsAutomaticAttachmentEnabled;

    /// <summary>
    /// Gets the toolbar preference, independently of automatic attachment.
    /// </summary>
    public bool IsToolbarEnabled => _settings.TextSelection.IsToolbarEnabled;

    /// <summary>
    /// Gets the combined monitoring intent, independently of optional overlay input observation.
    /// </summary>
    public bool IsMonitoringRequested => IsAutomaticAttachmentEnabled || IsToolbarEnabled;

    /// <summary>
    /// Gets whether either preference is waiting for a monitoring transition.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(SetToolbarEnabledCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetAutomaticAttachmentEnabledCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>
    /// Gets whether either preference is not waiting for a monitoring transition.
    /// </summary>
    public bool IsNotBusy => !IsBusy;

    private readonly Settings _settings;
    private readonly ITextSelectionWatcher _watcher;
    private readonly ILogger<TextSelectionController> _logger;

    /// <summary>
    /// Creates the application-wide coordinator for acknowledged text-selection preferences.
    /// </summary>
    public TextSelectionController(Settings settings, ITextSelectionWatcher watcher, ILogger<TextSelectionController> logger)
    {
        _settings = settings;
        _watcher = watcher;
        _logger = logger;
        watcher.RestorationRejected += HandleRestorationRejected;
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task SetToolbarEnabledAsync(bool isEnabled) => SetEnabledAsync(isEnabled, IsAutomaticAttachmentEnabled, CommitToolbar);

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task SetAutomaticAttachmentEnabledAsync(bool isEnabled) => SetEnabledAsync(isEnabled, IsToolbarEnabled, CommitAutomaticAttachment);

    private async Task SetEnabledAsync(bool isEnabled, bool isOtherConsumerEnabled, Action<bool> commit)
    {
        if (IsBusy)
        {
            return;
        }

        var shouldMonitor = isEnabled || isOtherConsumerEnabled;
        if (shouldMonitor == _watcher.IsEnabled)
        {
            commit(isEnabled);
            return;
        }

        IsBusy = true;
        try
        {
            try
            {
                await _watcher.SetEnabledCommand.ExecuteAsync(shouldMonitor);
            }
            catch
            {
                // Disabling the final consumer stops local delivery even if the Host cannot acknowledge it.
                if (!shouldMonitor)
                {
                    commit(isEnabled);
                }

                throw;
            }

            commit(isEnabled);
        }
        catch (TextSelectionMonitoringRejectedException exception)
        {
            var message = exception.Result.Message?.ToString() ?? LocaleKey.ChatWindowSettings_TextSelectionMonitoring_SetupFailed.I18N();
            ToastManager.Error(LocaleKey.Common_Error.I18N(), message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out while changing text-selection monitoring to {ShouldMonitor}.", shouldMonitor);
            ToastManager.Error(LocaleKey.Common_Error.I18N(), LocaleKey.ChatWindowSettings_TextSelectionMonitoring_SetupFailed.I18N());
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to change text-selection monitoring to {ShouldMonitor}.", shouldMonitor);
            ToastManager.Error(LocaleKey.Common_Error.I18N(), HandledSystemException.Handle(exception).GetFriendlyMessage().ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CommitToolbar(bool isEnabled)
    {
        _settings.TextSelection.IsToolbarEnabled = isEnabled;
        OnPropertyChanged(nameof(IsToolbarEnabled));
        OnPropertyChanged(nameof(IsMonitoringRequested));
    }

    private void CommitAutomaticAttachment(bool isEnabled)
    {
        _settings.TextSelection.IsAutomaticAttachmentEnabled = isEnabled;
        OnPropertyChanged(nameof(IsAutomaticAttachmentEnabled));
        OnPropertyChanged(nameof(IsMonitoringRequested));
    }

    private void HandleRestorationRejected(TextSelectionMonitoringControlResult result)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            // Neither consumer can remain advertised as active after native monitoring was rejected.
            CommitAutomaticAttachment(false);
            CommitToolbar(false);
        });
    }

    /// <inheritdoc />
    public void Dispose() => _watcher.RestorationRejected -= HandleRestorationRejected;
}