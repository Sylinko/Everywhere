using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.Win32.Input;
using Everywhere.Interop;
using Serilog;
using Windows.System;
using Windows.UI.Core;
using RawInputModifiers = Avalonia.Input.RawInputModifiers;

namespace Everywhere.Windows.Interop;

/// <summary>
/// A single UI-thread capture lease. Native hit testing uses immutable thread and window identities.
/// </summary>
internal sealed class WindowsWindowInputCapture(Window window, CoreWindowInputHost host, WindowsTextInputMethod method) : IWindowInputCapture
{
    public Window Target => window;

    public bool IsActive => _isActive;

    public bool IsComposing => _isActive && method.IsComposing;

    public event EventHandler? Lost;

    private readonly uint _inputThread = TextInputNative.GetWindowThreadProcessId(host.Handle, out _);
    private readonly nint _targetHandle = window.TryGetPlatformHandle()?.Handle ?? 0;
    private bool _isActive;
    private bool _hasRequestedCapture;

    public void Start()
    {
        Dispatcher.UIThread.VerifyAccess();
        var foreground = TextInputNative.GetForegroundWindow();
        if (foreground == 0)
        {
            throw new InvalidOperationException("Input capture requires a foreground source window.");
        }

        host.Capture = this;
        _isActive = true;
        window.PropertyChanged += HandleWindowPropertyChanged;
        window.Closed += HandleWindowClosed;
        window.PositionChanged += HandlePositionChanged;
        window.ScalingChanged += HandleScalingChanged;
        host.Window.KeyDown += HandleKeyDown;
        host.Window.KeyUp += HandleKeyUp;
        method.FocusLost += HandleFocusLost;
        // The host was prepared while the passive toolbar was visible. Only this later user
        // interaction creates the edit context and requests native delegation. The caller focuses
        // the attached Avalonia editor afterwards, which supplies its TextInputMethodClient.
        method.Start();
        _hasRequestedCapture = true;
        host.RequestCapture();
        if (TextInputNative.GetFocus() != host.Handle || TextInputNative.GetForegroundWindow() != foreground)
        {
            throw new InvalidOperationException("Input capture did not preserve foreground and establish host focus.");
        }
    }

    public bool ContainsInputPopup(PixelPoint point)
    {
        // Called by the existing native observation hook, not by the UI thread.
        return Volatile.Read(ref _isActive) && TextInputNative.IsInputPopup(point, _inputThread, _targetHandle);
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_isActive)
        {
            return;
        }

        Volatile.Write(ref _isActive, false);
        window.PropertyChanged -= HandleWindowPropertyChanged;
        window.Closed -= HandleWindowClosed;
        window.PositionChanged -= HandlePositionChanged;
        window.ScalingChanged -= HandleScalingChanged;
        host.Window.KeyDown -= HandleKeyDown;
        host.Window.KeyUp -= HandleKeyUp;
        method.FocusLost -= HandleFocusLost;

        try
        {
            method.Stop();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to close non-activating window text input context");
        }
        finally
        {
            // Stop the CoreText context before releasing the route. Do not activate the source
            // window to "restore" typing: it has remained foreground throughout this lease.
            host.Capture = null;
            if (_hasRequestedCapture)
            {
                _hasRequestedCapture = false;
                try
                {
                    host.ReleaseCapture();
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "Failed to release non-activating window input capture");
                }
            }
        }
    }

    private void HandleWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Visual.IsVisibleProperty && !window.IsVisible ||
            args.Property == Window.WindowStateProperty && window.WindowState == WindowState.Minimized)
        {
            LoseCapture();
        }
    }

    private void HandleWindowClosed(object? sender, EventArgs args) => LoseCapture();

    private void HandlePositionChanged(object? sender, PixelPointEventArgs args) => method.LayoutChanged();

    private void HandleScalingChanged(object? sender, EventArgs args) => method.LayoutChanged();

    private void HandleFocusLost()
    {
        // Leave the native CoreText callback before notifying owners that may tear down the view.
        Dispatcher.UIThread.Post(LoseCapture);
    }

    /// <summary>
    /// Revokes the lease and notifies its owner after native loss or a window input-policy change.
    /// </summary>
    public void LoseCapture()
    {
        if (!_isActive)
        {
            return;
        }

        Dispose();
        Lost?.Invoke(this, EventArgs.Empty);
    }

    private void HandleKeyDown(CoreWindow sender, KeyEventArgs args) => ForwardKey(args, RawKeyEventType.KeyDown);

    private void HandleKeyUp(CoreWindow sender, KeyEventArgs args) => ForwardKey(args, RawKeyEventType.KeyUp);

    private void ForwardKey(KeyEventArgs args, RawKeyEventType type)
    {
        if (!_isActive || method.IsComposing)
        {
            return;
        }

        var virtualKey = (int)args.VirtualKey;
        var status = args.KeyStatus;
        var keyData = (int)(status.ScanCode << 16) | (status.IsExtendedKey ? 1 << 24 : 0);
        var modifiers = RawInputModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= RawInputModifiers.Control;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= RawInputModifiers.Shift;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= RawInputModifiers.Alt;
        }

        // CoreText can mark native key events handled without delivering navigation to Avalonia.
        // Text is exclusively delivered by CoreText. Forward editing/navigation and shortcuts,
        // not CharacterReceived or a second WM_CHAR stream.
        if (virtualKey is not (8 or 9 or 13 or 27 or 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 46) &&
            (modifiers & (RawInputModifiers.Control | RawInputModifiers.Alt)) == 0)
        {
            return;
        }

        args.Handled = method.DispatchKey(
            KeyInterop.KeyFromVirtualKey(virtualKey, keyData),
            KeyInterop.PhysicalKeyFromVirtualKey(virtualKey, keyData),
            type,
            modifiers);
    }

    private bool IsDown(VirtualKey key) => (host.Window.GetKeyState(key) & CoreVirtualKeyStates.Down) != 0;
}
