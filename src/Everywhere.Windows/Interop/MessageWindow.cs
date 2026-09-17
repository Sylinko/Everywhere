using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.WinRT;
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

    private const uint InvokeMessage = (uint)WINDOW_MESSAGE.WM_APP + 1;

    private readonly ConcurrentQueue<Action> _invocations = new();
    private readonly ManualResetEventSlim _windowCreatedEvent = new(false);
    private readonly Lock _lock = new();
    // Message delivery vastly outnumbers subscriptions. Mutations publish a new
    // contiguous snapshot so dispatch only copies the ImmutableArray wrapper.
    private readonly Dictionary<uint, ImmutableArray<MessageHandler>> _handlers = new();
    private readonly WNDPROC _windowProcedure;
    private readonly string _windowClassName = $"Everywhere.MessageWindow.{Guid.NewGuid():N}";

    // ReSharper disable once NotAccessedField.Local
    private DispatcherQueueController? _dispatcherQueueController;
    private uint _threadId;
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

    /// <summary>Executes one synchronous operation on the message-window thread.</summary>
    public Task<T> InvokeAsync<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (PInvoke.GetCurrentThreadId() == _threadId)
        {
            try
            {
                return Task.FromResult(operation());
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _invocations.Enqueue(() =>
        {
            if (completion.Task.IsCompleted) return;
            try
            {
                completion.TrySetResult(operation());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        if (!PInvoke.PostMessage(HWnd, InvokeMessage, default, default))
        {
            completion.TrySetException(new Win32Exception(Marshal.GetLastWin32Error(), "Failed to dispatch work to the shared message window."));
        }

        return completion.Task;
    }

    /// <summary>Executes one operation on the message-window thread and waits for it to finish.</summary>
    public void Invoke(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        InvokeAsync(() =>
        {
            operation();
            return true;
        }).GetAwaiter().GetResult();
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
            _threadId = PInvoke.GetCurrentThreadId();
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

            PInvoke.CreateDispatcherQueueController(
                new DispatcherQueueOptions
                {
                    apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE,
                    threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
                    dwSize = (uint)Unsafe.SizeOf<DispatcherQueueOptions>()
                },
                out _dispatcherQueueController).ThrowOnFailure();

            _windowCreatedEvent.Set();

            while (true)
            {
                MSG msg;
                var result = PInvoke.GetMessage(&msg, HWND.Null, 0, 0);
                if (result < 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The shared message-window loop failed.");
                }
                if (result == 0) break;

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
        if (message == InvokeMessage)
        {
            while (_invocations.TryDequeue(out var invocation)) invocation();
            return default;
        }

        var msg = new MSG { hwnd = hWnd, message = message, wParam = wParam, lParam = lParam };
        ImmutableArray<MessageHandler> handlers;
        lock (_lock)
        {
            handlers = _handlers.GetValueOrDefault(message, []);
        }

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

        return PInvoke.DefWindowProc(hWnd, message, wParam, lParam);
    }
}
