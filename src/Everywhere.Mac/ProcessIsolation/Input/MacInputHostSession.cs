using System.Threading.Channels;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Hosts.Diagnostics;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Utilities;

namespace Everywhere.Mac.ProcessIsolation.Input;

/// <summary>
/// Owns one authenticated macOS Input connection. Native registrations and
/// capture state are released before the role acknowledges shutdown.
/// </summary>
public sealed class MacInputHostSession : IProcessRoleSession
{
    private const int EventQueueCapacity = 64;

    private AtomicBoolean IsDisposed => new(ref _isDisposed);
    private AtomicBoolean IsDraining => new(ref _isDraining);

    private readonly Channel<QueuedInputEvent> _events = Channel.CreateBounded<QueuedInputEvent>(
        new BoundedChannelOptions(EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly Lock _drainGate = new();
    private readonly Lock _queueGate = new();
    private readonly Lock _stateGate = new();

    private Task? _drainTask;
    private int _isDisposed;
    private int _isDraining;
    private CGInputHook? _input;
    private Task _sendTask = Task.CompletedTask;

    /// <inheritdoc />
    public void Bind(RpcConnection connection)
    {
        var diagnostics = new HostDiagnosticsRpcClient(connection);
        InputHostRpcBinding.Bind(connection, new InputHostRpcHandler(this, diagnostics));
        _sendTask = SendNotificationsAsync(new InputHostNotificationRpcClient(connection));
    }

    /// <inheritdoc />
    public void OnAuthenticated(RpcHandshake peer)
    {
        // macOS does not need the Windows foreground-permission transfer.
    }

    private ApplyInputStateResponse ApplyState(ApplyInputStateRequest request, IHostDiagnosticsRpc diagnostics)
    {
        Exception? failure = null;
        CGInputHook? failedInput = null;
        lock (_stateGate)
        {
            if (IsDraining)
            {
                return new ApplyInputStateResponse { IsApplied = false };
            }

            try
            {
                _input ??= new CGInputHook(TryQueue);
                _input.ApplyState(request);
            }
            catch (Exception exception)
            {
                failure = exception;
                failedInput = _input;
                _input = null;
            }
        }

        if (failedInput is not null)
        {
            try
            {
                failedInput.Dispose();
            }
            catch (Exception exception)
            {
                failure = new AggregateException(
                    failure ?? new InvalidOperationException("The macOS Input Host state failed before cleanup."),
                    exception);
            }
        }

        if (failure is null)
        {
            return new ApplyInputStateResponse { IsApplied = true };
        }

        diagnostics.LogAsync(
                new HostLogNotification
                {
                    Level = HostLogLevel.Error,
                    Source = nameof(MacInputHostSession),
                    Message = "Failed to apply the macOS Input Host state.",
                    ExceptionText = failure.ToString()
                })
            .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        return new ApplyInputStateResponse { IsApplied = false };
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

    private bool TryQueue(CGInputHook.InputEvent inputEvent)
    {
        lock (_queueGate)
        {
            if (IsDraining)
            {
                return false;
            }

            return _events.Writer.TryWrite(new QueuedInputEvent(inputEvent, DateTimeOffset.UtcNow.UtcTicks));
        }
    }

    private async Task SendNotificationsAsync(InputHostNotificationRpcClient client)
    {
        var sequence = 0UL;
        await foreach (var queuedEvent in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (queuedEvent.Event.Kind)
            {
                case CGInputHook.InputEventKind.ShortcutTriggered:
                    await client.ShortcutTriggeredAsync(
                            new ShortcutTriggeredNotification
                            {
                                RegistrationId = queuedEvent.Event.Id,
                                Sequence = ++sequence,
                                UtcTicks = queuedEvent.UtcTicks
                            })
                        .ConfigureAwait(false);
                    break;

                case CGInputHook.InputEventKind.CaptureChanged:
                    await SendCaptureChangedAsync(client, queuedEvent, ++sequence).ConfigureAwait(false);
                    break;

                case CGInputHook.InputEventKind.CaptureFinished:
                    // One local queue item keeps the final changed value and
                    // completion adjacent in the connection's FIFO stream.
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
                    throw new InvalidOperationException($"Unknown macOS Input event kind: {queuedEvent.Event.Kind}.");
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
        lock (_queueGate)
        {
            IsDraining.FlipIfFalse();
        }

        CGInputHook? input;
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
            // The sender must always observe completion, even when native cleanup
            // reports an exception, so role shutdown cannot wait forever on it.
            lock (_queueGate)
            {
                _events.Writer.TryComplete();
            }
        }

        await _sendTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Binds connection-owned collaborators once, keeping the native session free
    /// from nullable RPC clients during normal event processing.
    /// </summary>
    private sealed class InputHostRpcHandler(MacInputHostSession owner, IHostDiagnosticsRpc diagnostics) : IInputHostRpc
    {
        /// <inheritdoc />
        public ValueTask<ApplyInputStateResponse> ApplyStateAsync(
            ApplyInputStateRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(owner.ApplyState(request, diagnostics));
    }

    private readonly record struct QueuedInputEvent(CGInputHook.InputEvent Event, long UtcTicks);
}