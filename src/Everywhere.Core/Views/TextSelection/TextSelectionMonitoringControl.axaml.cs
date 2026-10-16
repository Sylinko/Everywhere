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
    TextSelectionController controller,
    ILogger<TextSelectionMonitoringControl> logger
) : TemplatedControl
{
    /// <summary>
    /// Gets the shared monitoring state and operation observed by every settings control.
    /// </summary>
    public TextSelectionController Controller => controller;

    /// <summary>
    /// Gets or sets the consumer changed by this settings switch.
    /// </summary>
    public TextSelectionFeature Feature { get; init; }

    public static readonly DirectProperty<TextSelectionMonitoringControl, bool> IsMonitoringEnabledProperty =
        AvaloniaProperty.RegisterDirect<TextSelectionMonitoringControl, bool>(nameof(IsMonitoringEnabled), control => control.IsMonitoringEnabled);

    public bool IsMonitoringEnabled
    {
        get;
        private set => SetAndRaise(IsMonitoringEnabledProperty, ref field, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        controller.PropertyChanged += HandleControllerChanged;
        UpdateMonitoringState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        controller.PropertyChanged -= HandleControllerChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void HandleControllerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateMonitoringState();
    }

    private void UpdateMonitoringState()
    {
        IsMonitoringEnabled = Feature == TextSelectionFeature.Toolbar ? controller.IsToolbarEnabled : controller.IsAutomaticAttachmentEnabled;
    }

    [RelayCommand]
    private async Task ChangeMonitoringAsync(ToggleSwitch sender)
    {
        var shouldEnable = sender.IsChecked ?? false;
        sender.SetCurrentValue(ToggleButton.IsCheckedProperty, IsMonitoringEnabled);
        var change = new TextSelectionFeatureChange(Feature, shouldEnable);
        if (!controller.SetEnabledCommand.CanExecute(change))
        {
            return;
        }

        try
        {
            await controller.SetEnabledCommand.ExecuteAsync(change);
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