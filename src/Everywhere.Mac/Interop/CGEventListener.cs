using CoreFoundation;
using Everywhere.Utilities;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Listens to global CGEvents such as keyboard and mouse events.
/// </summary>
public class CGEventListener : IDisposable
{
    /// <summary>
    /// Delegate for handling CGEvents. Set cgEventRef to zero to swallow the event.
    /// </summary>
    public delegate void CGEventHandler(CGEventType type, CGEvent cgEvent, ref nint cgEventRef);

    /// <summary>
    /// The default event listener with standard permissions. It can intercept and modify events.
    /// </summary>
    public static CGEventListener Default => DefaultListener.Value;

    /// <summary>
    /// The event listener with listen-only permissions. It cannot intercept or modify events. But handlers can take longer time to process events.
    /// </summary>
    public static CGEventListener ListenOnly => ListenOnlyListener.Value;

    public event CGEventHandler? EventReceived;

    private const CGEventMask DefaultMask =
        CGEventMask.KeyDown | CGEventMask.KeyUp | CGEventMask.FlagsChanged |
        CGEventMask.LeftMouseDown | CGEventMask.LeftMouseUp |
        CGEventMask.RightMouseDown | CGEventMask.RightMouseUp |
        CGEventMask.OtherMouseDown | CGEventMask.OtherMouseUp;

    // Creating a scoped listener must not also initialize both process-lifetime singleton taps.
    private static readonly Lazy<CGEventListener> DefaultListener = new(() => new CGEventListener(CGEventTapOptions.Default, DefaultMask));
    private static readonly Lazy<CGEventListener> ListenOnlyListener = new(() => new CGEventListener(CGEventTapOptions.ListenOnly, DefaultMask));

    private readonly CGEventTapOptions _options;
    private readonly CGEventMask _mask;
    private readonly Thread _workerThread;
    private readonly AutoResetEvent _readySignal = new(false);

    private CFMachPort? _eventTap;
    private CFRunLoopSource? _runLoopSource;
    private Exception? _startupException;
    private CFRunLoop? _runLoop;
    private ShutdownSource? _shutdownSource;
    private int _isDisposed;

    /// <summary>
    /// Starts a scoped native event session. Dispose it to remove its tap and stop its dedicated thread.
    /// Existing process-lifetime consumers may continue using Default and ListenOnly.
    /// </summary>
    public CGEventListener(CGEventTapOptions options, CGEventMask mask)
    {
        _options = options;
        _mask = mask;
        _workerThread = new Thread(RunLoopThread)
        {
            Name = "CGEventListenerThread",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _workerThread.Start();
        _readySignal.WaitOne();

        if (_startupException is not null)
        {
            Dispose();
            throw new InvalidOperationException("The macOS CoreGraphics event tap failed to start.", _startupException);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        // Signal a run-loop source rather than calling Stop before Run has necessarily begun.
        // A signaled source remains pending across that startup race and requires no polling.
        if (_startupException is null)
        {
            _shutdownSource?.Signal();
            _runLoop?.WakeUp();
        }
        if (Thread.CurrentThread != _workerThread && !_workerThread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The macOS event listener did not stop within the cleanup deadline.");
        }

        _readySignal.Dispose();
    }

    private void RunLoopThread()
    {
        try
        {
            _eventTap = CreateTap();
            _runLoopSource = _eventTap.CreateRunLoopSource() ??
                throw new InvalidOperationException("The macOS CoreGraphics event tap run-loop source could not be created.");
            _runLoop = CFRunLoop.Current;
            _shutdownSource = new ShutdownSource(_runLoop);
            _runLoop.AddSource(_runLoopSource, CFRunLoop.ModeDefault);
            _runLoop.AddSource(_shutdownSource, CFRunLoop.ModeDefault);
        }
        catch (Exception exception)
        {
            _startupException = exception;
        }
        finally
        {
            // Always release the constructor wait, including permission and
            // native startup failures.
            _readySignal.Set();
        }

        try
        {
            if (_startupException is null) CFRunLoop.Current.Run();
        }
        finally
        {
            if (_runLoop is { } runLoop)
            {
                if (_runLoopSource is { } inputSource) runLoop.RemoveSource(inputSource, CFRunLoop.ModeDefault);
                if (_shutdownSource is { } shutdownSource) runLoop.RemoveSource(shutdownSource, CFRunLoop.ModeDefault);
            }
            _eventTap?.Invalidate();
            DisposeHelper.DisposeToDefault(ref _runLoopSource);
            DisposeHelper.DisposeToDefault(ref _shutdownSource);
            DisposeHelper.DisposeToDefault(ref _eventTap);
        }
    }

    private CFMachPort CreateTap()
    {
        var tap = CGEvent.CreateTap(
            CGEventTapLocation.HID,
            CGEventTapPlacement.HeadInsert,
            _options,
            _mask,
            HandleEvent,
            IntPtr.Zero);

        return tap ?? throw new InvalidOperationException("CGEvent tap creation failed.");
    }

    private nint HandleEvent(nint proxy, CGEventType type, nint cgEventRef, nint userData)
    {
        var originalEventRef = cgEventRef;

        // Early exit if the event tap is not initialized
        if (Volatile.Read(ref _isDisposed) != 0 || _eventTap is null) return cgEventRef;

        try
        {
            if (type is CGEventType.TapDisabledByTimeout or CGEventType.TapDisabledByUserInput)
            {
                CGEvent.TapEnable(_eventTap);
                return cgEventRef;
            }

            using var cgEvent = CGInterop.CGEventFromHandle(cgEventRef);
            EventReceived?.Invoke(type, cgEvent, ref cgEventRef);
        }
        catch
        {
            // Native callbacks must fail open even if a subscriber or wrapper
            // throws after changing the event reference.
            return originalEventRef;
        }

        return cgEventRef;
    }

    private sealed class ShutdownSource(CFRunLoop runLoop) : CFRunLoopSourceCustom
    {
        protected override void OnSchedule(CFRunLoop loop, NSString mode) { }

        protected override void OnCancel(CFRunLoop loop, NSString mode) { }

        protected override void OnPerform() => runLoop.Stop();
    }
}