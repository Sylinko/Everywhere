using System.Collections.Concurrent;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.Linux.Interop.X11Backend;
using Microsoft.Extensions.Logging;
using X11;
using AvaloniaWindow = Avalonia.Controls.Window;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop;

/// <summary>
/// Facade for X11 backend components.
/// </summary>
public sealed class X11WindowBackend : IWindowBackend, IEventHelper
{
    private readonly ConcurrentDictionary<X11Window, IVisualElement> _windowCache = new();

    // Components
    public X11Context Context { get; }
    public X11CoreServices CoreServices { get; }
    public X11InputHandler InputHandler { get; }
    public X11WindowManager WindowManager { get; }
    public X11Screenshot Screenshot { get; }
    public X11SelectionHandler SelectionHandler { get; }

    public X11WindowBackend(ILogger<X11WindowBackend> logger)
    {
        // 1. Initialize Context (Thread & Display)
        Context = new X11Context(logger);

        if (Context.Display == IntPtr.Zero)
        {
            // Handle fatal error appropriately
            logger.LogError("X11 Backend failed to initialize context");
        }

        // 2. Initialize Services
        CoreServices = new X11CoreServices(Context);
        InputHandler = new X11InputHandler(logger, Context, CoreServices);
        WindowManager = new X11WindowManager(logger, Context, CoreServices);
        Screenshot = new X11Screenshot(Context);
        SelectionHandler = new X11SelectionHandler(logger, Context, CoreServices);
    }

    ~X11WindowBackend()
    {
        Context.Dispose();
    }

    // Helper to cache/create visual elements
    public IVisualElement GetWindowElement(X11Window window)
    {
        if (_windowCache.TryGetValue(window, out var cached)) return cached;
        var element = new X11WindowVisualElement(this, window);
        _windowCache[window] = element;
        return element;
    }

    public IVisualElement GetScreenElement()
    {
        int screenIdx = Xlib.XDefaultScreen(Context.Display);
        return new X11ScreenVisualElement(this, screenIdx);
    }

    public IVisualElement? GetFocusedWindowElement()
    {
        var focusWin = X11Window.None;
        var revert = RevertFocus.RevertToNone;
        Xlib.XGetInputFocus(Context.Display, ref focusWin, ref revert);
        return focusWin != X11Window.None ? GetWindowElement(focusWin) : null;
    }

    public IVisualElement GetWindowElementAt(PixelPoint point)
    {
        var win = WindowManager.GetWindowAtPoint(point.X, point.Y);
        return GetWindowElement(win == X11Window.None ? Context.RootWindow : win);
    }

    public IVisualElement? GetWindowElementByInfo(int pid, PixelRect rect)
    {
        X11Window target = X11Window.None;
        WindowManager.ForEachTopLevelWindow(w =>
        {
            if (WindowManager.GetWindowPid(w) == pid && WindowManager.GetWindowBounds(w) == rect) target = w;
        });
        return target != X11Window.None ? GetWindowElement(target) : null;
    }

    public IVisualElement.ICapturedBitmapData Capture(IVisualElement? window, PixelRect rect)
    {
        var handle = window?.NativeWindowHandle != null ? (X11Window)window.NativeWindowHandle : Context.RootWindow;
        return Screenshot.Capture(handle, rect);
    }

    public PixelPoint GetPointer()
    {
        var window = X11Window.None;
        var child = X11Window.None;
        int rx = 0, ry = 0, wx = 0, wy = 0;
        uint mask = 0;
        Xlib.XQueryPointer(Context.Display, Context.RootWindow, ref window, ref child, ref rx, ref ry, ref wx, ref wy, ref mask);
        return new PixelPoint(rx, ry);
    }

    public void SetPickerWindow(AvaloniaWindow? window)
    {
        var handle = (X11Window?)window?.TryGetPlatformHandle()?.Handle ?? X11Window.None;
        WindowManager.ScanSkipWindow = handle;
    }

    public bool GetKeyState(KeyModifiers keyModifier)
    {
        return WindowManager.GetKeyState(keyModifier);
    }

    public int GrabKey(KeyboardShortcut hotkey, Action handler) => InputHandler.GrabKey(hotkey, handler);
    public void UngrabKey(int id) => InputHandler.UngrabKey(id);
    public void GrabKeyHook(Action<KeyboardShortcut, EventType> hook) => InputHandler.GrabKeyHook(hook);
    public void UngrabKeyHook() => InputHandler.UngrabKeyHook();
    public int GrabMouse(MouseShortcut hotkey, Action handler) => throw new NotImplementedException();
    public void UngrabMouse(int id) => throw new NotImplementedException();
    public void GrabMouseHook(Action<PixelPoint, EventType> hook) => InputHandler.GrabMouseHook(hook);
    public void UngrabMouseHook() => InputHandler.UngrabMouseHook();

    public void SetFocusable(AvaloniaWindow window, bool focusable)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } x11Handle)
            WindowManager.SetFocusable((X11Window)x11Handle, focusable);
    }

    public void SetHitTestVisible(AvaloniaWindow window, bool visible)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } x11Handle)
        {
            var width = (ushort)window.Width;
            var height = (ushort)window.Height;
            WindowManager.SetHitTestVisible((X11Window)x11Handle, visible, width, height);
        }
    }

    public void SetOverrideRedirect(AvaloniaWindow window, bool redirect)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } x11Handle)
            WindowManager.SetOverrideRedirect((X11Window)x11Handle, redirect);
    }

    public void SetCloaked(AvaloniaWindow window, bool cloaked)
    {
        if (cloaked) window.Hide();
        else
        {
            window.Show();
            window.Activate();
        }
        Xlib.XFlush(Context.Display);
    }

    public bool GetEffectiveVisible(AvaloniaWindow window)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } x11Handle)
            return WindowManager.GetEffectiveVisible((X11Window)x11Handle);
        return false;
    }

    public bool AnyModelDialogOpened(AvaloniaWindow window)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } x11Handle)
            return WindowManager.AnyModelDialogOpened((X11Window)x11Handle);
        return false;
    }

    public void RequestUserAttention(AvaloniaWindow window)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } x11Handle)
            WindowManager.RequestUserAttention((X11Window)x11Handle);
    }

    public void InitializeWindow(AvaloniaWindow window)
    {

    }

    public void SendKeyboardShortcut(KeyboardShortcut shortcut)
    {
        InputHandler.SendKeyboardShortcut(shortcut);
    }

    public IEnumerable<IVisualElement> Screens => [GetScreenElement()];

    public IDisposable Subscribe(IObserver<TextSelectionData> observer)
    {
        return SelectionHandler
            .Select(text => new TextSelectionData(text, GetFocusedWindowElement()))
            .Subscribe(observer);
    }
}