using System.Threading.Channels;
using Windows.Win32;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Hosts.Diagnostics;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Utilities;

namespace Everywhere.Windows.ProcessIsolation.Input;

/// <summary>
/// Owns one authenticated Windows Input connection. Native registrations never
/// escape this session, and draining removes them before Host shutdown is acknowledged.
/// </summary>
public sealed class WindowsInputHostSession : IProcessRoleSession
{
    private AtomicBoolean IsDisposed => new(ref _isDisposed);
    private AtomicBoolean IsDraining => new(ref _isDraining);

    private readonly Channel<QueuedInputEvent> _events = Channel.CreateBounded<QueuedInputEvent>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly Lock _drainGate = new();
    private readonly Lock _stateGate = new();

    private Task? _drainTask;
    private int _isDisposed;
    private int _isDraining;
    private WindowsInputHook? _input;
    private int _mainProcessId;
    private Task _sendTask = Task.CompletedTask;

    /// <inheritdoc />
    public void Bind(RpcConnection connection)
    {
        var diagnostics = new HostDiagnosticsRpcClient(connection);
        InputHostRpcBinding.Bind(connection, new InputHostRpcHandler(this, diagnostics));
        _sendTask = SendNotificationsAsync(new InputHostNotificationRpcClient(connection));
    }

    /// <inheritdoc />
    public void OnAuthenticated(RpcHandshake peer) =>
        Volatile.Write(ref _mainProcessId, (int)peer.ProcessId);

    private ApplyInputStateResponse ApplyState(ApplyInputStateRequest request, IHostDiagnosticsRpc diagnostics)
    {
        lock (_stateGate)
        {
            if (IsDraining)
            {
                return new ApplyInputStateResponse { IsApplied = false };
            }

            _input ??= new WindowsInputHook(TryQueue, diagnostics);
            _input.ApplyState(request);
        }

        return new ApplyInputStateResponse { IsApplied = true };
    }

    /// <inheritdoc />
    public ValueTask BeginDrainingAsync(CancellationToken cancellationToken = default)
    {
        Task drainTask;
        lock (_drainGate)
        {
            drainTask = _drainTask ??= DrainAsync();
        }

        return new ValueTask(drainTask.WaitAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!IsDisposed.FlipIfFalse())
        {
            return;
        }

        await BeginDrainingAsync().ConfigureAwait(false);
    }

    private bool TryQueue(WindowsInputHook.InputEvent inputEvent)
    {
        if (IsDraining)
        {
            return false;
        }

        var mainProcessId = Volatile.Read(ref _mainProcessId);
        if (inputEvent.Kind == WindowsInputHook.InputEventKind.ShortcutTriggered && mainProcessId > 0)
        {
            // The Host received the user's input, so Windows may grant it the
            // foreground privilege. Transfer that short-lived privilege to Main
            // before publishing the shortcut notification.
            PInvoke.AllowSetForegroundWindow((uint)mainProcessId);
        }

        return _events.Writer.TryWrite(new QueuedInputEvent(inputEvent, DateTimeOffset.UtcNow.UtcTicks));
    }

    private async Task SendNotificationsAsync(InputHostNotificationRpcClient client)
    {
        var sequence = 0UL;
        await foreach (var queuedEvent in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (queuedEvent.Event.Kind)
            {
                case WindowsInputHook.InputEventKind.ShortcutTriggered:
                    await client.ShortcutTriggeredAsync(
                            new ShortcutTriggeredNotification
                            {
                                RegistrationId = queuedEvent.Event.Id,
                                Sequence = ++sequence,
                                UtcTicks = queuedEvent.UtcTicks
                            })
                        .ConfigureAwait(false);
                    break;

                case WindowsInputHook.InputEventKind.CaptureChanged:
                    await SendCaptureChangedAsync(client, queuedEvent, ++sequence).ConfigureAwait(false);
                    break;

                case WindowsInputHook.InputEventKind.CaptureFinished:
                    // Windows reports the final changed value and completion from
                    // one native callback. One local queue item keeps that pair atomic.
                    await SendCaptureChangedAsync(client, queuedEvent, ++sequence).ConfigureAwait(false);
                    await client.CaptureFinishedAsync(
                            new ShortcutCaptureFinishedNotification
                            {
                                CaptureId = queuedEvent.Event.Id,
                                Sequence = ++sequence,
                                UtcTicks = queuedEvent.UtcTicks,
                                Key = queuedEvent.Event.Key,
                                Modifiers = queuedEvent.Event.Modifiers
                            })
                        .ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown Windows Input event kind: {queuedEvent.Event.Kind}.");
            }
        }
    }

    private static ValueTask SendCaptureChangedAsync(InputHostNotificationRpcClient client, QueuedInputEvent queuedEvent, ulong sequence) =>
        client.CaptureChangedAsync(
            new ShortcutCaptureChangedNotification
            {
                CaptureId = queuedEvent.Event.Id,
                Sequence = sequence,
                UtcTicks = queuedEvent.UtcTicks,
                Key = queuedEvent.Event.Key,
                Modifiers = queuedEvent.Event.Modifiers
            });

    private async Task DrainAsync()
    {
        IsDraining.FlipIfFalse();

        WindowsInputHook? input;
        lock (_stateGate)
        {
            input = _input;
            _input = null;
        }

        try
        {
            input?.Dispose();
        }
        finally
        {
            // The sender must always observe completion. Otherwise a native cleanup
            // failure would leave the role runner waiting on this session forever.
            _events.Writer.TryComplete();
        }

        await _sendTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Binds connection-owned collaborators once, so the session and native hook
    /// do not carry nullable RPC clients through their normal operation paths.
    /// </summary>
    private sealed class InputHostRpcHandler(WindowsInputHostSession owner, IHostDiagnosticsRpc diagnostics) : IInputHostRpc
    {
        /// <inheritdoc />
        public ValueTask<ApplyInputStateResponse> ApplyStateAsync(
            ApplyInputStateRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(owner.ApplyState(request, diagnostics));
    }

    private readonly record struct QueuedInputEvent(WindowsInputHook.InputEvent Event, long UtcTicks);
}