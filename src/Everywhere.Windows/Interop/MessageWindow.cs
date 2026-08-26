using System.ComponentModel;
using System.Reactive.Disposables;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using System.Collections.Immutable;

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
    // Message delivery vastly outnumbers subscriptions. Mutations publish a new
    // contiguous snapshot so dispatch only copies the ImmutableArray wrapper.
    private readonly Dictionary<uint, ImmutableArray<MessageHandler>> _handlers = new();
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
            var handlers = _handlers.GetValueOrDefault(message, []);
            _handlers[message] = handlers.Add(handler);
        }

        return Disposable.Create(() => RemoveHandler(message, handler));
    }

    private void RemoveHandler(uint message, MessageHandler handler)
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(message, out var handlers))
            {
                return;
            }

            handlers = handlers.Remove(handler);
            if (handlers.IsEmpty)
            {
                _handlers.Remove(message);
            }
            else
            {
                _handlers[message] = handlers;
            }
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
        ImmutableArray<MessageHandler> handlers;
        lock (_lock)
        {
            handlers = _handlers.GetValueOrDefault(msg.message, []);
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
