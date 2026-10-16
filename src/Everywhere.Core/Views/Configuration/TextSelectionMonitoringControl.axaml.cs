using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
/// Changes automatic text-selection monitoring through an acknowledged Host transition.
/// </summary>
public sealed partial class TextSelectionMonitoringControl(
    ITextSelectionWatcher watcher,
    ILogger<TextSelectionMonitoringControl> logger
) : TemplatedControl
{
    /// <summary>
    /// Gets the shared monitoring state and operation observed by every settings control.
    /// </summary>
    public ITextSelectionWatcher Watcher => watcher;

    [RelayCommand]
    private async Task ChangeMonitoringAsync(ToggleSwitch sender)
    {
        var shouldEnable = sender.IsChecked ?? false;
        sender.SetCurrentValue(ToggleButton.IsCheckedProperty, watcher.IsEnabled);
        if (!watcher.SetEnabledCommand.CanExecute(shouldEnable))
        {
            return;
        }

        try
        {
            await watcher.SetEnabledCommand.ExecuteAsync(shouldEnable);
        }
        catch (TextSelectionMonitoringRejectedException exception)
        {
            var message = exception.Result.Message?.ToString() ?? LocaleKey.ChatWindowSettings_TextSelectionMonitoring_SetupFailed.I18N();
            ToastManager.Error(LocaleKey.Common_Error.I18N(), message);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Timed out while changing automatic text-selection monitoring to {ShouldEnable}.", shouldEnable);
            ToastManager.Error(
                LocaleKey.Common_Error.I18N(),
                LocaleKey.ChatWindowSettings_TextSelectionMonitoring_SetupFailed.I18N());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to change automatic text-selection monitoring to {ShouldEnable}.", shouldEnable);
            ToastManager.Error(LocaleKey.Common_Error.I18N(), HandledSystemException.Handle(exception).GetFriendlyMessage().ToString());
        }
    }
}