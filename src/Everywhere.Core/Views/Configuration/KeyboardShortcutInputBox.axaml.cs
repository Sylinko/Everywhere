using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Utilities;

namespace Everywhere.Views;

public interface IShortcutPart;

public record TextShortcutPart(string Text) : IShortcutPart;

public record IconShortcutPart(Geometry Icon) : IShortcutPart;

[TemplatePart(ClearButtonPartName, typeof(Button), IsRequired = true)]
public partial class KeyboardShortcutInputBox : TemplatedControl
{
    private const string ClearButtonPartName = "PART_ClearButton";

    private static readonly Geometry WindowsIcon = StreamGeometry.Parse("M17 0V17H0V0H17Zm0 38H0V21H17V38ZM21 0H38V17H21V0ZM38 21V38H21V21H38Z");
    private static readonly Geometry CopilotIcon = StreamGeometry.Parse(
        "m564 110c43 0 82 26 98 66l2 6 20 61c4 13 17 22 31 22 30 0 59 7 82 27 22 20 32 46 35 75 6 56-12 132-46 223-15 40-41 84-60 116-23 38-64 60-107 63h-8-223a105 105 0 01-98-66l-2-6-20-61a32 32 0 00-31-22c-30 0-59-7-82-27-22-20-32-46-35-75-6-56 12-132 46-223 15-40 41-84 60-116 23-38 64-60 107-63h8zm153 228h-31c-35 0-66 22-78 54l-2 6-85 283-2 7-3 7h96c22 0 42-11 52-28 19-30 41-70 54-104l9-24a921 921 0 0017-53l5-19c10-40 13-70 11-92-2-17-7-25-12-29-4-4-11-7-25-8zM466 610c-6 1-12 2-19 3h-10-99l20 60a32 32 0 0027 22h4 15c20 0 38-12 46-31l2-5zm-29-427h-96c-22 0-42 11-52 28-19 30-41 70-54 104l-9 24a886 886 0 00-17 53l-5 19c-10 40-13 70-11 92 2 17 7 25 12 29 4 4 11 7 25 8h6 31c35 0 66-22 78-54l2-6 85-283 2-7zm117 155h-38c-29 0-55 19-64 48l-35 116a159 159 0 01-18 39h38c29 0 55-19 64-48l35-116a159 159 0 0118-39m10-155h-15c-20 0-38 12-46 31l-2 5-15 49c6-1 12-2 19-3h10 100l-20-60a32 32 0 00-27-22z");

    public static readonly StyledProperty<KeyboardShortcut> ShortcutProperty =
        AvaloniaProperty.Register<KeyboardShortcutInputBox, KeyboardShortcut>(nameof(Shortcut));

    public KeyboardShortcut Shortcut
    {
        get => GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    public static readonly DirectProperty<KeyboardShortcutInputBox, bool> IsRecordingProperty =
        AvaloniaProperty.RegisterDirect<KeyboardShortcutInputBox, bool>(nameof(IsRecording), o => o.IsRecording);

    public bool IsRecording
    {
        get;
        private set => SetAndRaise(IsRecordingProperty, ref field, value);
    }

    public static readonly DirectProperty<KeyboardShortcutInputBox, AvaloniaList<IShortcutPart>> ShortcutPartsProperty =
        AvaloniaProperty.RegisterDirect<KeyboardShortcutInputBox, AvaloniaList<IShortcutPart>>(
            nameof(ShortcutParts),
            o => o.ShortcutParts);

    public AvaloniaList<IShortcutPart> ShortcutParts { get; } = [];

    private IDisposable? _clearButtonClickedSubscription;
    private bool _isPressed;
    private KeyboardShortcut _shortcutBeforePressed;
    private IKeyboardShortcutScope? _shortcutScope;

    public KeyboardShortcutInputBox()
    {
        UpdateShortcutParts(Shortcut);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ShortcutProperty)
        {
            UpdateShortcutParts(change.NewValue as KeyboardShortcut? ?? default);
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DisposeHelper.DisposeToDefault(ref _clearButtonClickedSubscription);
        _clearButtonClickedSubscription =
            e.NameScope.Find<Button>(ClearButtonPartName)?.AddDisposableHandler(Button.ClickEvent, HandleClearButtonClicked);
    }

    private void HandleClearButtonClicked(object? sender, RoutedEventArgs e)
    {
        Shortcut = default;
        e.Handled = true;
    }

    private void UpdateShortcutParts(KeyboardShortcut shortcut)
    {
        ShortcutParts.Clear();
        if (shortcut.IsEmpty) return;
        if (shortcut.Key == Key.Escape)
        {
            TopLevel.GetTopLevel(this)?.Focus(); // Cancel
            return;
        }

#if IsOSX
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Meta)) ShortcutParts.Add(new TextShortcutPart("⌘"));
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Control)) ShortcutParts.Add(new TextShortcutPart("⌃"));
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Alt)) ShortcutParts.Add(new TextShortcutPart("⌥"));
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Shift)) ShortcutParts.Add(new TextShortcutPart("⇧"));
#else
        if (shortcut is { Modifiers: (KeyModifiers.Shift | KeyModifiers.Meta), Key: Key.F23 })
        {
            ShortcutParts.Add(new IconShortcutPart(CopilotIcon));
            return;
        }

        if (shortcut.Modifiers.HasFlag(KeyModifiers.Meta)) ShortcutParts.Add(new IconShortcutPart(WindowsIcon));
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Control)) ShortcutParts.Add(new TextShortcutPart("Ctrl"));
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Alt)) ShortcutParts.Add(new TextShortcutPart("Alt"));
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Shift)) ShortcutParts.Add(new TextShortcutPart("Shift"));
#endif

        if (shortcut.Key != Key.None)
        {
            ShortcutParts.Add(new TextShortcutPart(shortcut.Key.ToString()));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _isPressed = true;
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_isPressed || e.InitialPressMouseButton != MouseButton.Left) return;

        _isPressed = false;

        if (!this.GetVisualsAt(e.GetPosition(this)).Any(c => this == c || this.IsVisualAncestorOf(c))) return;

        EnterRecordingMode();
        e.Handled = true;
    }

    [RelayCommand]
    private void EnterRecordingMode()
    {
        if (_shortcutScope is not null) return;

        _shortcutScope = ServiceLocator.Resolve<IShortcutListener>().StartCaptureKeyboardShortcut();
        _shortcutScope.PressingShortcutChanged += HandleShortcutScopePressingShortcutChanged;
        _shortcutScope.ShortcutFinished += HandleShortcutScopeShortcutFinished;

        _shortcutBeforePressed = Shortcut;
        Shortcut = default;
        IsRecording = true;
        Focus();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        IsRecording = false;

        if (_shortcutBeforePressed.IsValid)
        {
            Shortcut = _shortcutBeforePressed;
        }

        UpdateShortcutParts(Shortcut);

        _shortcutScope?.PressingShortcutChanged -= HandleShortcutScopePressingShortcutChanged;
        _shortcutScope?.ShortcutFinished -= HandleShortcutScopeShortcutFinished;
        DisposeHelper.DisposeToDefault(ref _shortcutScope);
    }

    private void HandleShortcutScopePressingShortcutChanged(IKeyboardShortcutScope _, KeyboardShortcut hotkey) =>
        Dispatcher.UIThread.Invoke(() =>
        {
            UpdateShortcutParts(hotkey);
            _shortcutBeforePressed = default;
        });

    private void HandleShortcutScopeShortcutFinished(IKeyboardShortcutScope _, KeyboardShortcut shortcut)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            Shortcut = shortcut;
            TopLevel.GetTopLevel(this)?.Focus();
        });

        DisposeHelper.DisposeToDefault(ref _shortcutScope);
    }
}