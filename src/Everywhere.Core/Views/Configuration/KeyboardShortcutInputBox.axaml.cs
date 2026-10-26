using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Utilities;

namespace Everywhere.Views;

/// <summary>
/// Records keyboard shortcuts and delegates their visual presentation to KeyboardShortcutPresenter.
/// </summary>
[TemplatePart(ClearButtonPartName, typeof(Button), IsRequired = true)]
public partial class KeyboardShortcutInputBox : TemplatedControl
{
    private const string ClearButtonPartName = "PART_ClearButton";

    /// <summary>
    /// Defines the configured shortcut, independently of the unfinished recording display.
    /// </summary>
    public static readonly StyledProperty<KeyboardShortcut> ShortcutProperty =
        AvaloniaProperty.Register<KeyboardShortcutInputBox, KeyboardShortcut>(nameof(Shortcut));

    /// <summary>
    /// Gets or sets the configured shortcut.
    /// </summary>
    public KeyboardShortcut Shortcut
    {
        get => GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    /// <summary>
    /// Defines whether this control currently owns a shortcut capture scope.
    /// </summary>
    public static readonly DirectProperty<KeyboardShortcutInputBox, bool> IsRecordingProperty =
        AvaloniaProperty.RegisterDirect<KeyboardShortcutInputBox, bool>(nameof(IsRecording), o => o.IsRecording);

    /// <summary>
    /// Gets whether a shortcut is being recorded.
    /// </summary>
    public bool IsRecording
    {
        get;
        private set => SetAndRaise(IsRecordingProperty, ref field, value);
    }

    /// <summary>
    /// Defines the shortcut currently shown by the embedded presenter during recording.
    /// </summary>
    public static readonly DirectProperty<KeyboardShortcutInputBox, KeyboardShortcut> DisplayShortcutProperty =
        AvaloniaProperty.RegisterDirect<KeyboardShortcutInputBox, KeyboardShortcut>(
            nameof(DisplayShortcut),
            o => o.DisplayShortcut);

    /// <summary>
    /// Gets the displayed shortcut, which may be an unfinished combination while recording.
    /// </summary>
    public KeyboardShortcut DisplayShortcut
    {
        get;
        private set => SetAndRaise(DisplayShortcutProperty, ref field, value);
    }

    private IDisposable? _clearButtonClickedSubscription;
    private bool _isPressed;
    private KeyboardShortcut _shortcutBeforePressed;
    private IKeyboardShortcutScope? _shortcutScope;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ShortcutProperty)
        {
            UpdateDisplayShortcut(change.GetNewValue<KeyboardShortcut>());
        }
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DisposeHelper.DisposeToDefault(ref _clearButtonClickedSubscription);
        _clearButtonClickedSubscription =
            e.NameScope.Find<Button>(ClearButtonPartName)?.AddDisposableHandler(Button.ClickEvent, HandleClearButtonClicked);
    }

    private void HandleClearButtonClicked(object? sender, RoutedEventArgs e)
    {
        StopRecording(shouldRestoreShortcut: false);
        Shortcut = default;
        e.Handled = true;
    }

    private void UpdateDisplayShortcut(KeyboardShortcut shortcut)
    {
        if (shortcut.Key == Key.Escape)
        {
            // Escape cancels recording here, never in the display-only presenter.
            DisplayShortcut = default;
            TopLevel.GetTopLevel(this)?.Focus();
            return;
        }

        DisplayShortcut = shortcut;
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPressed = true;
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_isPressed || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        _isPressed = false;

        if (!this.GetVisualsAt(e.GetPosition(this)).Any(c => this == c || this.IsVisualAncestorOf(c)))
        {
            return;
        }

        EnterRecordingMode();
        e.Handled = true;
    }

    [RelayCommand]
    private void EnterRecordingMode()
    {
        if (_shortcutScope is not null)
        {
            return;
        }

        _shortcutScope = ServiceLocator.Resolve<IShortcutListener>().StartCaptureKeyboardShortcut();
        _shortcutScope.PressingShortcutChanged += HandleShortcutScopePressingShortcutChanged;
        _shortcutScope.ShortcutFinished += HandleShortcutScopeShortcutFinished;

        _shortcutBeforePressed = Shortcut;
        DisplayShortcut = default;
        IsRecording = true;
        Focus();
    }

    /// <inheritdoc />
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        StopRecording(shouldRestoreShortcut: true);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopRecording(shouldRestoreShortcut: true);
        base.OnDetachedFromVisualTree(e);
    }

    private void StopRecording(bool shouldRestoreShortcut)
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
#if LINUX
        _pendingShortcut = default;
#endif
        _shortcutScope?.PressingShortcutChanged -= HandleShortcutScopePressingShortcutChanged;
        _shortcutScope?.ShortcutFinished -= HandleShortcutScopeShortcutFinished;
        DisposeHelper.DisposeToDefault(ref _shortcutScope);

        if (shouldRestoreShortcut)
        {
            Shortcut = _shortcutBeforePressed;
        }
        UpdateDisplayShortcut(Shortcut);
    }

    private void HandleShortcutScopePressingShortcutChanged(IKeyboardShortcutScope scope, KeyboardShortcut hotkey)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (IsRecording && ReferenceEquals(_shortcutScope, scope))
            {
                UpdateDisplayShortcut(hotkey);
            }
        });
    }

    private void HandleShortcutScopeShortcutFinished(IKeyboardShortcutScope scope, KeyboardShortcut shortcut)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (!IsRecording || !ReferenceEquals(_shortcutScope, scope))
            {
                return;
            }

            // Keep the configured value until a complete chord is accepted. A partial
            // modifier, cancellation or disconnected Host must not erase the old setting.
            Shortcut = shortcut.IsValid && shortcut.Key != Key.Escape ? shortcut : _shortcutBeforePressed;
            StopRecording(shouldRestoreShortcut: false);
            TopLevel.GetTopLevel(this)?.Focus();
        });
    }
}