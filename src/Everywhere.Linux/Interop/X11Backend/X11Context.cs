using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Tmds.Linux;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Manages the X11 Display connection, the dedicated X11 thread, and the IO loop.
/// </summary>
public sealed class X11Context : IDisposable
{
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _ops = new(new ConcurrentQueue<Action>());
    private Thread? _xThread;
    private int _wakePipeR = -1;
    private int _wakePipeW = -1;
    private volatile bool _running;
    
    public IntPtr Display { get; private set; }
    public X11Window RootWindow { get; private set; }
    public X11Window MessageWindow { get; private set; }
    public AtomCache AtomCache { get; private set; } = null!;
    private  IEnumerable<string> PredefinedAtoms = new[]
    {
        "ATOM",
        "PRIMARY",
        "SECONDARY",
        "CLIPBOARD",
        "WM_HINTS",
        "WM_PROTOCOLS",
        "WM_DELETE_WINDOW",
        "WM_TAKE_FOCUS",
        "UTF8_STRING",
        "_NET_WM_NAME",
        "_NET_WM_ICON",
        "_NET_WM_PID",
        "_NET_ACTIVE_WINDOW",
        "_NET_WM_WINDOW_TYPE",
        "_NET_WM_WINDOW_TYPE_NORMAL",
        "_NET_WM_STATE",
        "_NET_WM_STATE_FULLSCREEN",
        "_NET_WM_STATE_ABOVE",
        "_NET_WM_STATE_HIDDEN",
        "_NET_WM_STATE_DEMANDS_ATTENTION",
        "_NET_WM_BYPASS_COMPOSITOR",
        "_NET_SUPPORTED",
        "_NET_CLIENT_LIST",
        "_NET_CLIENT_LIST_STACKING",
        "_NET_FRAME_EXTENTS",
        "_MOTIF_WM_HINTS"
    };

    // Fired on XThread when an event arrives
    public event Action<IntPtr>? XEventReceived;

    public X11Context(ILogger logger)
    {
        _logger = logger;
        Initialize();
    }

    private void Initialize()
    {
        Display = Xlib.XOpenDisplay(Environment.GetEnvironmentVariable("DISPLAY"));
        if (Display == IntPtr.Zero)
        {
            _logger.LogError("Failed to open X Display.");
            return;
        }

        AtomCache = new AtomCache(Display, PredefinedAtoms);
        RootWindow = Xlib.XDefaultRootWindow(Display);
        MessageWindow = Xlib.XCreateSimpleWindow(Display, RootWindow, -10, -10, 1, 1, 0, 0, 0);

        // Select key events on root window
        Xlib.XSelectInput(
            Display,
            RootWindow,
            EventMask.KeyPressMask | EventMask.KeyReleaseMask
            | EventMask.ButtonPressMask | EventMask.ButtonReleaseMask | EventMask.ButtonMotionMask
            | EventMask.FocusChangeMask);

        InitializeWakePipe();

        _running = true;
        _xThread = new Thread(XThreadMain) { IsBackground = true, Name = "X11DisplayThread" };
        _xThread.Start();
    }

    private void InitializeWakePipe()
    {
        try
        {
            var fds = new int[2];
            unsafe
            {
                fixed (int* pfds = fds)
                {
                    if (LibC.pipe(pfds) == 0)
                    {
                        _wakePipeR = fds[0];
                        _wakePipeW = fds[1];
                        ConfigurePipeFd(_wakePipeR);
                        ConfigurePipeFd(_wakePipeW);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create wake pipe");
        }
    }

    private void ConfigurePipeFd(int fd)
    {
        var flags = LibC.fcntl(fd, LibC.F_GETFL, 0);
        LibC.fcntl(fd, LibC.F_SETFL, flags | LibC.O_NONBLOCK);
        LibC.fcntl(fd, LibC.F_SETFD, LibC.FD_CLOEXEC);
    }

    /// <summary>
    /// Enqueues an operation to be executed on the X11 thread.
    /// </summary>
    public void Invoke(Action action)
    {
        _ops.Add(action);
        WakeThread();
    }

    public T InvokeSync<T>(Func<T> func)
    {
        if (Thread.CurrentThread == _xThread)
        {
            return func();
        }
        var tcs = new TaskCompletionSource<T>();
        _ops.Add(() =>
        {
            try
            {
                var result = func();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        WakeThread();
        return tcs.Task.GetAwaiter().GetResult();
    }

    public void InvokeSync(Action action)
    {
        if (Thread.CurrentThread == _xThread)
        {
            action();
            return;
        }
        var tcs = new TaskCompletionSource<bool>();
        _ops.Add(() =>
        {
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        WakeThread();
        tcs.Task.GetAwaiter().GetResult();
    }

    public void XFlush()
    {
        Invoke(() => Xlib.XFlush(Display));
    }

    public Atom GetAtom(string name, bool onlyIfExists = false)
    {
        return InvokeSync(() => AtomCache.GetAtom(name, onlyIfExists));
    }

    private void WakeThread()
    {
        if (_wakePipeW == -1) return;
        try
        {
            var b = new byte[] { 1 };
            unsafe
            {
                fixed (byte* pb = b)
                {
                    LibC.write(_wakePipeW, pb, b.Length);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to write wake pipe"); }
    }

    private void XThreadMain()
    {
        Xlib.XSetErrorHandler(OnXError);
        var evPtr = Marshal.AllocHGlobal(256);
        var buf = new byte[4];

        try
        {
            var xfd = Xlib.XConnectionNumber(Display);
            var fds = new pollfd[2];
            fds[0].fd = xfd;
            fds[0].events = LibC.POLLIN;
            fds[1].fd = _wakePipeR;
            fds[1].events = LibC.POLLIN;

            while (_running || !_ops.IsCompleted)
            {
                // 1. Process pending ops
                while (_ops.TryTake(out var op))
                {
                    try { op(); } catch (Exception ex) { _logger.LogWarning(ex, "X op failed"); }
                }

                // 2. Poll
                unsafe
                {
                    fixed (pollfd* pfds = fds)
                    {
                        var rc = LibC.poll(pfds, (uint)fds.Length, -1);
                        if (rc <= 0) continue;

                        // Check wake pipe
                        if ((fds[1].revents & LibC.POLLIN) != 0)
                        {
                            DrainWakePipe(buf);
                            // Process ops again immediately after wake
                            while (_ops.TryTake(out var op))
                            {
                                try { op(); } catch (Exception ex) { _logger.LogWarning(ex, "X op failed"); }
                            }
                        }

                        // Check X Event
                        if ((fds[0].revents & LibC.POLLIN) != 0)
                        {
                            while (Xlib.XPending(Display) > 0)
                            {
                                Xlib.XNextEvent(Display, evPtr);
                                try
                                {
                                    XEventReceived?.Invoke(evPtr);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error processing XEvent");
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(evPtr);
        }
    }

    private void DrainWakePipe(byte[] buf)
    {
        unsafe
        {
            fixed (byte* pbuf = buf)
            {
                while (LibC.read(_wakePipeR, pbuf, buf.Length) > 0) { }
            }
        }
    }

    private int OnXError(IntPtr display, ref XErrorEvent ev)
    {
        try
        {
            string text;
            try
            {
                var buffer = new byte[256];
                unsafe
                {
                    fixed (byte* buff = buffer)
                    {
                        Xlib.XGetErrorText(display, ev.error_code, (IntPtr)buff, buffer.Length);
                    }
                }
                text = System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get X error text for code {code}", ev.error_code);
                text = $"Unknown error code {ev.error_code}";
            }
            _logger.LogError(
                "X Error: code={code}({errorName}) request={req}({reqName}) minor={minor} resource={res} text={text}",
                ev.error_code,
                X11Native.GetErrorCodeName(ev.error_code),
                ev.request_code,
                X11Native.GetRequestCodeName(ev.request_code),
                ev.minor_code,
                ev.resourceid,
                text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle X error");
        }
        return 0;
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            _ops.CompleteAdding();
            WakeThread();
            if (_xThread?.IsAlive == true) _xThread.Join(500);
            
            if (Display != IntPtr.Zero)
            {
                Xlib.XCloseDisplay(Display);
                Display = IntPtr.Zero;
            }
            if (_wakePipeR != -1) LibC.close(_wakePipeR);
            if (_wakePipeW != -1) LibC.close(_wakePipeW);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "X11 Context Dispose Error");
        }
    }
}
