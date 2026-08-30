using System.ComponentModel;
using System.Reactive.Disposables;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Hosts one shared hidden top-level window on a dedicated STA thread for Win32 message-based services.
/// </summary>
/// <remarks>
/// A hidden top-level window is used instead of <c>HWND_MESSAGE</c> so the host can receive broadcast messages such as <c>WM_DISPLAYCHANGE</c>.
/// </remarks>
internal sealed class MessageWindow
{
    public static MessageWindow Shared { get; } = new();

    public HWND HWnd { get; private set; }

    public delegate void MessageHandler(in MSG msg);

    private readonly ManualResetEventSlim _windowCreatedEvent = new(false);
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, List<MessageHandler>> _handlers = new();
    private readonly WNDPROC _windowProcedure;
    private readonly string _windowClassName = $"Everywhere.MessageWindow.{Guid.NewGuid():N}";
    private Exception? _windowCreationException;

    private MessageWindow()
    {
        _windowProcedure = WindowProcedure;
        var thread = new Thread(WindowLoop)
        {
            IsBackground = true,
            Name = "Everywhere.MessageWindow",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        _windowCreatedEvent.Wait();
        if (_windowCreationException is { } exception)
        {
            throw new InvalidOperationException("Failed to create the shared Win32 message window.", exception);
        }
    }

    public IDisposable AddHandler(uint message, MessageHandler handler)
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(message, out var list))
            {
                list = [];
                _handlers[message] = list;
            }
            list.Add(handler);
        }
        return Disposable.Create(() => RemoveHandler(message, handler));
    }

    private void RemoveHandler(uint message, MessageHandler handler)
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(message, out var list)) return;
            list.Remove(handler);
            if (list.Count == 0) _handlers.Remove(message);
        }
    }

    private unsafe void WindowLoop()
    {
        try
        {
            var moduleHandle = PInvoke.GetModuleHandle(default(PCWSTR));
            fixed (char* windowClassName = _windowClassName)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = _windowProcedure,
                    hInstance = moduleHandle,
                    lpszClassName = windowClassName,
                };
                if (PInvoke.RegisterClassEx(windowClass) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to register the shared message-window class.");
                }

                HWnd = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                    windowClassName,
                    windowClassName,
                    0,
                    0, 0, 0, 0,
                    hInstance: moduleHandle);
            }

            if (HWnd.IsNull)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the shared message window.");
            }

            _windowCreatedEvent.Set();

            MSG msg;
            while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0) != 0)
            {
                PInvoke.TranslateMessage(&msg);
                PInvoke.DispatchMessage(&msg);
            }
        }
        catch (Exception exception)
        {
            _windowCreationException = exception;
            _windowCreatedEvent.Set();
        }
    }

    private LRESULT WindowProcedure(HWND hWnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        MessageHandler[] handlers;
        lock (_lock)
        {
            handlers = _handlers.TryGetValue(message, out var registeredHandlers) ? [.. registeredHandlers] : [];
        }

        if (handlers.Length > 0)
        {
            var msg = new MSG { hwnd = hWnd, message = message, wParam = wParam, lParam = lParam };
            foreach (var handler in handlers)
            {
                try
                {
                    handler(in msg);
                }
                catch
                {
                    // A shared native message host must remain alive when an individual consumer fails.
                }
            }
        }

        return PInvoke.DefWindowProc(hWnd, message, wParam, lParam);
    }
}
