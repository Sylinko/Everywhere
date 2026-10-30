using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia;
using Everywhere.Automation;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Linux.Automation;

/// <summary>
/// Combines toolkit selection events and optional Wayland PRIMARY evidence without
/// global input grabs, regular-clipboard mutation or synthetic Copy operations.
/// </summary>
public sealed class LinuxTextSelectionMonitorFactory(LinuxVisualElementBackend backend) : ITextSelectionMonitorFactory
{
    /// <inheritdoc />
    public ITextSelectionMonitor Create(
        ITextSelectionMonitorContext context,
        int mainProcessId,
        TextSelectionMonitoringConfiguration configuration,
        TextSelectionDiagnostics diagnostics,
        Func<TextSelectionObservation, CancellationToken, ValueTask> publish)
    {
        return new Monitor(backend, context, mainProcessId, configuration, publish);
    }

    private sealed class Monitor : ITextSelectionMonitor
    {
        private static readonly string[] EventTypes = ["object:text-selection-changed", "object:text-caret-moved"];
        private readonly LinuxVisualElementBackend _backend;
        private readonly ITextSelectionMonitorContext _context;
        private readonly int _mainProcessId;
        private readonly Func<TextSelectionObservation, CancellationToken, ValueTask> _publish;

        private readonly Channel<SelectionSignal> _events = Channel.CreateBounded<SelectionSignal>(8);

        private readonly CancellationTokenSource _lifetime = new();
        private readonly AtspiNative.EventCallback _callback;
        private readonly nint _listener;
        private readonly Task _pump;
        private readonly PrimarySelectionMonitor _primary;
        private string? _lastText;
        private string? _lastWindow;
        private long _lastPublication;
        private TextSelectionMonitoringConfiguration _configuration;
        private int _isDisposed;
        public Monitor(
            LinuxVisualElementBackend backend,
            ITextSelectionMonitorContext context,
            int mainProcessId,
            TextSelectionMonitoringConfiguration configuration,
            Func<TextSelectionObservation, CancellationToken, ValueTask> publish)
        {
            _backend = backend;
            _context = context;
            _mainProcessId = mainProcessId;
            _configuration = configuration;
            _publish = publish;
            _callback = OnEvent;
            _listener = backend.Accessibility.Call(() =>
            {
                var listener = AtspiNative.atspi_event_listener_new(_callback, 0, 0);
                if (listener == 0)
                {
                    throw new InvalidOperationException("Could not create the accessibility selection listener.");
                }

                try
                {
                    foreach (var eventType in EventTypes)
                    {
                        AtspiNative.Checked(AtspiNative.atspi_event_listener_register(listener, eventType, out var error), error);
                    }

                    return listener;
                }
                catch
                {
                    AtspiNative.g_object_unref(listener);
                    throw;
                }
            });
            _primary = new PrimarySelectionMonitor(change => Queue(new SelectionSignal(0, change)));
            _pump = RunAsync();
        }

        public ValueTask UpdateConfigurationAsync(TextSelectionMonitoringConfiguration configuration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _configuration, configuration);
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            try
            {
                _backend.Accessibility.Call(() =>
                {
                    try
                    {
                        foreach (var eventType in EventTypes)
                        {
                            AtspiNative.Checked(AtspiNative.atspi_event_listener_deregister(_listener, eventType, out var error), error);
                        }
                    }
                    finally
                    {
                        AtspiNative.g_object_unref(_listener);
                    }

                    return true;
                });
            }
            catch (Exception exception)
            {
                Serilog.Log.Debug("Selection listener cleanup encountered {FailureType}.", exception.GetType().Name);
            }

            _lifetime.Cancel();
            _events.Writer.TryComplete();
            try
            {
                await _primary.DisposeAsync().ConfigureAwait(false);
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                while (_events.Reader.TryRead(out var source))
                {
                    _backend.Accessibility.Release(source.Source);
                }

                _lifetime.Dispose();
            }
        }

        private void OnEvent(nint nativeEvent, nint userData)
        {
            if (nativeEvent == 0)
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref _isDisposed) != 0)
                {
                    return;
                }

                var source = Marshal.ReadIntPtr(nativeEvent, IntPtr.Size);
                if (source == 0)
                {
                    return;
                }

                AtspiNative.g_object_ref(source);
                Queue(new SelectionSignal(source, null));
            }
            finally
            {
                AtspiNative.FreeEvent(nativeEvent);
            }
        }

        private void Queue(SelectionSignal signal)
        {
            if (Volatile.Read(ref _isDisposed) != 0)
            {
                _backend.Accessibility.Release(signal.Source);
                return;
            }

            // A drag can generate many intermediate ranges. Release discarded
            // native proxies explicitly and retain the newest evidence only.
            while (!_events.Writer.TryWrite(signal))
            {
                if (!_events.Reader.TryRead(out var previous))
                {
                    _backend.Accessibility.Release(signal.Source);
                    return;
                }

                _backend.Accessibility.Release(previous.Source);
            }
        }

        private async Task RunAsync()
        {
            await foreach (var initial in _events.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var signal = initial;
                try
                {
                    while (true)
                    {
                        await Task.Delay(150, _lifetime.Token).ConfigureAwait(false);
                        var hasNewerSignal = false;
                        while (_events.Reader.TryRead(out var next))
                        {
                            _backend.Accessibility.Release(signal.Source);
                            signal = next;
                            hasNewerSignal = true;
                        }

                        if (!hasNewerSignal)
                        {
                            break;
                        }
                    }

                    var evidence = await _context.ExecuteAsync((context, backend, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return ReadSelection(context, signal);
                    }, _lifetime.Token).ConfigureAwait(false);
                    if (evidence is null)
                    {
                        continue;
                    }

                    var observation = evidence.Observation;
                    await using (observation.ConfigureAwait(false))
                    {
                        // Native and PRIMARY events can describe the same gesture.
                        // Avoid remapping the toolbar twice, but allow a later
                        // deliberate selection of the same text to show it again.
                        var window = evidence.WindowAddress;
                        var now = Environment.TickCount64;
                        if (_lastText == observation.Text && _lastWindow == window && now - _lastPublication < 600)
                        {
                            continue;
                        }

                        await _publish(observation, _lifetime.Token).ConfigureAwait(false);
                        _lastText = observation.Text;
                        _lastWindow = window;
                        _lastPublication = now;
                    }
                }
                catch (Exception exception) when (
                    exception is VisualElementProviderException or TimeoutException or NotSupportedException or IOException)
                {
                    // One incomplete toolkit observation must not end monitoring
                    // for all later applications. No selected text is logged.
                }
                finally
                {
                    _backend.Accessibility.Release(signal.Source);
                }
            }
        }

        private SelectionEvidence? ReadSelection(VisualContext context, SelectionSignal signal)
        {
            var window = _backend.Compositor.GetActiveWindowAsync().GetAwaiter().GetResult();
            if (window is null || window.ProcessId == _mainProcessId || window.ProcessId <= 0)
            {
                return null;
            }

            var configuration = Volatile.Read(ref _configuration);
            if (configuration.IsFullscreenApplicationExcluded && window.IsFullscreen)
            {
                return null;
            }

            var path = File.ResolveLinkTarget($"/proc/{window.ProcessId}/exe", true)?.FullName;
            if (path is null || configuration.ExcludedApplications.Contains(path, StringComparer.Ordinal))
            {
                return null;
            }

            if (signal.Primary is { } primary)
            {
                if (string.IsNullOrWhiteSpace(primary.Text) || primary.ProcessId != window.ProcessId || primary.WindowAddress != window.Id)
                {
                    return null;
                }

                // PRIMARY proves selected text, not which accessible object owns
                // it. Never manufacture a remote visual anchor from later focus.
                var observation = new TextSelectionObservation(_context, primary.Text, isTextIncomplete: primary.IsIncomplete)
                {
                    ApplicationIdentity = path,
                    PointerPosition = new PixelPoint(primary.PointerX, primary.PointerY),
                };
                return new SelectionEvidence(observation, window.Id);
            }

            if (_backend.Accessibility.Call(() => AtspiNative.Pid(signal.Source)) != window.ProcessId)
            {
                return null;
            }

            var retention = context.CreateRetention();
            try
            {
                var element = AtspiVisualElement.GetOrCreate(retention, _backend, window, signal.Source);
                var text = element.GetSelectedText(65536);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                var result = element.Query(new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.ProcessId, 0));
                var pointer = _backend.Compositor.GetGlobalCursorPositionAsync().GetAwaiter().GetResult();
                var observation = new TextSelectionObservation(_context, text, new TextSelectionSource(retention, result), text.Length >= 32768)
                {
                    ApplicationIdentity = path,
                    PointerPosition = pointer,
                };
                retention = null;
                return new SelectionEvidence(observation, window.Id);
            }
            finally
            {
                retention?.Dispose();
            }
        }

        private sealed record SelectionEvidence(TextSelectionObservation Observation, string WindowAddress);

        private sealed record SelectionSignal(nint Source, PrimarySelectionChange? Primary);
    }
}