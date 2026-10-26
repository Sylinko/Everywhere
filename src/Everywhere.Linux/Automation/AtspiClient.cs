using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Everywhere.Automation;

namespace Everywhere.Linux.Automation;

/// <summary>
/// Owns libatspi and its GLib context on one thread. Native proxies and their releases never
/// cross this dispatcher. Individual D-Bus calls have a finite provider timeout.
/// </summary>
public sealed class AtspiClient : IDisposable
{
    private const string ActivityEvent = "object:state-changed:focused";

    private readonly BlockingCollection<Action> _work = new(64);
    private readonly Thread _thread;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isInitialized;
    private readonly AtspiNative.EventCallback _activityCallback;
    private nint _focusedElement;
    private nint _activityListener;

    /// <summary>
    /// Starts the native dispatcher and waits until it can accept accessibility work.
    /// </summary>
    public AtspiClient()
    {
        _activityCallback = OnActivity;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Everywhere AT-SPI",
        };
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs one accessibility operation on the native dispatcher and returns its result.
    /// </summary>
    public T Call<T>(Func<T> operation)
    {
        if (Thread.CurrentThread == _thread)
        {
            EnsureInitialized();
            return operation();
        }

        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_work.TryAdd(() =>
        {
            try
            {
                EnsureInitialized();
                result.SetResult(operation());
            }
            catch (Exception exception)
            {
                result.SetException(exception);
            }
        }))
        {
            throw new TimeoutException("The accessibility dispatcher is busy.");
        }

        return result.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Releases an owned native reference on the accessibility dispatcher.
    /// </summary>
    public void Release(nint pointer)
    {
        if (pointer != 0)
        {
            Call(() =>
            {
                AtspiNative.g_object_unref(pointer);
                return true;
            });
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_work.IsAddingCompleted)
        {
            return;
        }

        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(3));
    }

    private void Run()
    {
        try
        {
            _ready.SetResult();
            // Announce the assistive client when the Host starts, before newly
            // launched applications decide whether to expose their bridges.
            // An unavailable bus still permits compositor-only window access.
            try
            {
                EnsureInitialized();
            }
            catch (Exception exception)
            {
                Serilog.Log.Debug(
                    exception,
                    "Accessibility initialization failed; compositor access remains usable and semantic requests may retry.");
            }

            while (!_work.IsCompleted)
            {
                if (_work.TryTake(out var operation, 10))
                {
                    operation();
                }

                if (_isInitialized)
                {
                    for (var index = 0; index < 16 && AtspiNative.g_main_context_iteration(0, 0) != 0; index++)
                    {
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
        }
        finally
        {
            if (_activityListener != 0)
            {
                try
                {
                    AtspiNative.Checked(AtspiNative.atspi_event_listener_deregister(_activityListener, ActivityEvent, out var error), error);
                }
                catch (Exception exception)
                {
                    Serilog.Log.Debug(exception, "The accessibility registry disconnected before listener cleanup.");
                }
                finally
                {
                    AtspiNative.g_object_unref(_activityListener);
                }
            }

            if (_focusedElement != 0)
            {
                AtspiNative.g_object_unref(_focusedElement);
            }

            if (_isInitialized)
            {
                AtspiNative.atspi_exit();
            }
        }
    }

    private void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        // libatspi returns 2 when its bus cannot be opened. Calling its other APIs
        // in that state invokes g_error() and aborts the entire Automation Host.
        var status = AtspiNative.atspi_init();
        if (status is not (0 or 1))
        {
            AtspiNative.atspi_exit();
            throw new NotSupportedException("The AT-SPI accessibility bus is unavailable.");
        }

        AtspiNative.atspi_set_timeout(800, 1500);
        // Register as an assistive client, rather than merely opening the bus.
        // Toolkits use the registry's active listeners to enable their bridges.
        var listener = AtspiNative.atspi_event_listener_new(_activityCallback, 0, 0);
        if (listener == 0)
        {
            AtspiNative.atspi_exit();
            throw new NotSupportedException("The accessibility registry could not create a listener.");
        }

        try
        {
            if (AtspiNative.Checked(AtspiNative.atspi_event_listener_register(listener, ActivityEvent, out var error), error) == 0)
            {
                throw new NotSupportedException("The accessibility registry rejected the client registration.");
            }
        }
        catch
        {
            AtspiNative.g_object_unref(listener);
            AtspiNative.atspi_exit();
            throw;
        }

        _activityListener = listener;
        _isInitialized = true;
    }

    /// <summary>
    /// Returns an owned reference to the last focused descendant only after its
    /// current state and parent chain confirm membership in the requested window.
    /// The cache is native-dispatcher-owned and never retains a visual Context.
    /// </summary>
    public nint GetFocusedDescendant(nint root)
    {
        return Call(() =>
        {
            if (_focusedElement == 0)
            {
                return 0;
            }

            var deadline = Stopwatch.StartNew();
            var visited = new HashSet<nint>();
            var references = new List<nint>();
            try
            {
                if (!AtspiNative.HasState(_focusedElement, AtspiState.Focused))
                {
                    return 0;
                }

                var current = AtspiNative.g_object_ref(_focusedElement);
                while (current != 0 && references.Count < AtspiQueryLimits.MaximumNodes
                    && deadline.ElapsedMilliseconds < AtspiQueryLimits.MaximumElapsedMilliseconds)
                {
                    references.Add(current);
                    if (current == root)
                    {
                        return AtspiNative.g_object_ref(_focusedElement);
                    }

                    if (!visited.Add(current))
                    {
                        break;
                    }

                    current = AtspiNative.Checked(AtspiNative.atspi_accessible_get_parent(current, out var error), error);
                    if (current != 0
                        && (references.Count >= AtspiQueryLimits.MaximumNodes
                            || deadline.ElapsedMilliseconds >= AtspiQueryLimits.MaximumElapsedMilliseconds))
                    {
                        AtspiNative.g_object_unref(current);
                        break;
                    }
                }

                return 0;
            }
            catch (Exception exception) when (exception is TimeoutException or VisualElementProviderException)
            {
                return 0;
            }
            finally
            {
                foreach (var reference in references)
                {
                    AtspiNative.g_object_unref(reference);
                }
            }
        });
    }

    private void OnActivity(nint nativeEvent, nint userData)
    {
        if (nativeEvent == 0)
        {
            return;
        }

        try
        {
            var source = Marshal.ReadIntPtr(nativeEvent, IntPtr.Size);
            var isFocused = Marshal.ReadInt32(nativeEvent, IntPtr.Size * 2) != 0;
            if (source == 0)
            {
                return;
            }

            if (!isFocused && source != _focusedElement)
            {
                return;
            }

            var next = isFocused ? AtspiNative.g_object_ref(source) : 0;
            if (_focusedElement != 0)
            {
                AtspiNative.g_object_unref(_focusedElement);
            }

            _focusedElement = next;
        }
        finally
        {
            AtspiNative.FreeEvent(nativeEvent);
        }
    }
}