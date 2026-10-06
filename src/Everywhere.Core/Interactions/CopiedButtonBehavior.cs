using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Utilities;
using Lucide.Avalonia;
using ShadUI;

namespace Everywhere.Interactions;

/// <summary>
/// Executes a copy button's command and temporarily displays successful completion.
/// Ordinary Click handlers are suppressed; flyouts and handled-event listeners are unaffected.
/// </summary>
public sealed class CopiedButtonBehavior : Behavior<Button>
{
    // The class handler runs before instance Click handlers. Store the owner on the button
    // so clicks on unrelated buttons require no behavior collection allocation.
    private static readonly AttachedProperty<CopiedButtonBehavior?> OwnerProperty =
        AvaloniaProperty.RegisterAttached<CopiedButtonBehavior, Button, CopiedButtonBehavior?>("CopyFeedbackOwner");

    private IDisposable? _feedbackTimer;
    private IDisposable? _hitTestOverride;
    private IDisposable? _iconOverride;
    private IDisposable? _contentOverride;

    static CopiedButtonBehavior()
    {
        // This registration intentionally lasts for the application lifetime.
        Button.ClickEvent.AddClassHandler<Button>(HandleClick);
    }

    /// <inheritdoc />
    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject is not { } button) return;
        button.SetValue(OwnerProperty, this);
        button.DetachedFromVisualTree += HandleDetachedFromVisualTree;
    }

    /// <inheritdoc />
    protected override void OnDetaching()
    {
        if (AssociatedObject is { } button)
        {
            button.DetachedFromVisualTree -= HandleDetachedFromVisualTree;
            button.ClearValue(OwnerProperty);
        }

        ResetFeedback();
        base.OnDetaching();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEnabledProperty && !IsEnabled) ResetFeedback();
    }

    private static void HandleClick(Button button, RoutedEventArgs args)
    {
        if (!ReferenceEquals(args.Source, button) || button.GetValue(OwnerProperty) is not { IsEnabled: true } behavior) return;

        args.Handled = true;
        if (behavior._hitTestOverride is not null) return;
        behavior.ExecuteCopyAsync(button).Detach();
    }

    private async Task ExecuteCopyAsync(Button button)
    {
        try
        {
            var command = button.Command;
            var parameter = button.CommandParameter;
            if (command?.CanExecute(parameter) is not true) return;

            _hitTestOverride = button.SetValue(InputElement.IsHitTestVisibleProperty, false, BindingPriority.Animation);
            if (command is IAsyncRelayCommand asyncCommand)
            {
                await asyncCommand.ExecuteAsync(parameter);
            }
            else
            {
                command.Execute(parameter);
            }

            // Removing/disabling the behavior or detaching the button clears this override.
            if (_hitTestOverride is null) return;
            if (ButtonAssist.GetIcon(button) is not null)
            {
                _iconOverride = button.SetValue(
                    ButtonAssist.IconProperty,
                    new LucideIcon
                    {
                        Kind = LucideIconKind.Check,
                        Foreground = Brushes.Green
                    },
                    BindingPriority.Animation);
            }
            if (button.Content is not null)
            {
                _contentOverride = button.SetValue(
                    ContentControl.ContentProperty,
                    LocaleKey.Common_Copied.I18N(),
                    BindingPriority.Animation);
            }

            _feedbackTimer = DispatcherTimer.RunOnce(ResetFeedback, TimeSpan.FromSeconds(1.5d), DispatcherPriority.Background);
        }
        catch (Exception exception)
        {
            ResetFeedback();
            ToastManager.Error(LocaleKey.Common_CopyFailed.I18N(), exception.GetFriendlyMessage());
        }
    }

    private void HandleDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs args) => ResetFeedback();

    private void ResetFeedback()
    {
        DisposeHelper.DisposeToDefault(ref _feedbackTimer);
        DisposeHelper.DisposeToDefault(ref _iconOverride);
        DisposeHelper.DisposeToDefault(ref _contentOverride);
        DisposeHelper.DisposeToDefault(ref _hitTestOverride);
    }
}