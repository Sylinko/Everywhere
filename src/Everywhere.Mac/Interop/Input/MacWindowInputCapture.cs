using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Everywhere.Interop;
using ObjCRuntime;
using Serilog;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Owns one non-activating panel input session. AppKit routes keys and IME input to the existing
/// Avalonia view; source application activation is neither changed nor restored by this lease.
/// All operations and native key notifications belong to the UI thread.
/// </summary>
internal sealed partial class MacWindowInputCapture(
    Window window,
    NSWindow nativeWindow,
    NSView nativeView,
    Action<MacWindowInputCapture> onReleased
) : IWindowInputCapture
{
    /// <summary>
    /// Gets the window that owns this input session.
    /// </summary>
    public Window Target { get; } = window;

    /// <inheritdoc />
    public bool IsActive { get; private set; }

    /// <inheritdoc />
    public bool IsComposing =>
        IsActive && nativeView.RespondsToSelector(HasMarkedTextSelector) &&
        GetHasMarkedText(nativeView.Handle, HasMarkedTextSelector.Handle);

    /// <inheritdoc />
    public event EventHandler? Lost;

    private static readonly Selector HasMarkedTextSelector = new("hasMarkedText");

    private NSObject? _keyObserver;
    private bool _isDisposed;

    /// <summary>
    /// Acquires the panel's key route after the owner has validated its source and visibility.
    /// </summary>
    public bool Start()
    {
        Dispatcher.UIThread.VerifyAccess();
        IsActive = true;
        Target.PropertyChanged += HandleWindowPropertyChanged;
        Target.Closed += HandleWindowClosed;
        _keyObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidResignKeyNotification, _ => LoseCapture(), nativeWindow);

        // NSPanel acquires AppKit's non-activating key route. Do not activate NSApplication.
        nativeWindow.MakeKeyAndOrderFront(null);
        return nativeWindow.IsKeyWindow && nativeWindow.MakeFirstResponder(nativeView);
    }

    /// <summary>
    /// Releases the route and notifies its owner of involuntary input loss.
    /// </summary>
    public void LoseCapture()
    {
        if (!IsActive) return;
        Dispose();
        Lost?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isDisposed) return;

        _isDisposed = true;
        IsActive = false;
        Target.PropertyChanged -= HandleWindowPropertyChanged;
        Target.Closed -= HandleWindowClosed;

        if (_keyObserver is { } observer)
        {
            _keyObserver = null;
            NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
            observer.Dispose();
        }

        try
        {
            // Reset through Avalonia so both native marked text and editor preedit are cleared.
            Target.PlatformImpl?.TryGetFeature<ITextInputMethodImpl>()?.Reset();
        }
        finally
        {
            try
            {
                if (nativeWindow.IsKeyWindow) nativeWindow.ResignKeyWindow();
            }
            finally
            {
                onReleased(this);
            }
        }
    }

    private void HandleWindowClosed(object? sender, EventArgs e) => LoseCapture();

    private void HandleWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == InputElement.IsKeyboardFocusWithinProperty && Target.IsKeyboardFocusWithin && IsActive)
        {
            Log.Debug(
                "macOS panel editor received logical focus. Current source {SourceProcessId}, native key {IsKeyWindow}",
                NSWorkspace.SharedWorkspace.FrontmostApplication.ProcessIdentifier,
                nativeWindow.IsKeyWindow);
        }

        if (e.Property == Visual.IsVisibleProperty && !Target.IsVisible ||
            e.Property == Window.WindowStateProperty && Target.WindowState == WindowState.Minimized)
        {
            LoseCapture();
        }
    }

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool GetHasMarkedText(nint receiver, nint selector);
}