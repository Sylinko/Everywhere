using System.Threading.Channels;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Utilities;
using Microsoft.Extensions.Logging;

namespace Everywhere.ProcessIsolation.Input;

/// <summary>
/// Main-side <see cref="IShortcutListener"/> proxy. Main owns callback delegates
/// and desired state; each Input connection receives a complete serializable snapshot.
/// </summary>
public sealed class InputHostShortcutListener : IShortcutListener, IAsyncDisposable
{
    private static readonly TimeSpan EventStaleThreshold = TimeSpan.FromSeconds(2);

    private AtomicBoolean IsDisposed => new(ref _isDisposed);

    private readonly IHostConnectionSource _connectionSource;
    private readonly Dictionary<KeyboardShortcut, HandlerGroup> _keyboardRegistrations = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger<InputHostShortcutListener> _logger;
    private readonly Dictionary<MouseShortcut, HandlerGroup> _mouseRegistrations = [];
    private readonly Dictionary<ulong, HandlerGroup> _registrationsById = [];
    private readonly Lock _stateGate = new();
    private readonly Channel<byte> _stateChanges = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly Channel<Action> _callbacks = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
    private readonly Task _callbackTask;
    private readonly Task _runTask;

    private RpcConnection? _activeConnection;
    private RemoteKeyboardShortcutScope? _captureScope;
    private int _isDisposed;
    private ulong _lastSequence;
    private ulong _nextCaptureId;
    private ulong _nextRegistrationId;

    /// <summary>Starts restoring desired Input state to the current and replacement connections.</summary>
    internal InputHostShortcutListener(IHostConnectionSource connectionSource, ILogger<InputHostShortcutListener> logger)
    {
        _connectionSource = connectionSource;
        _logger = logger;
        _callbackTask = RunCallbacksAsync();
        _runTask = RunAsync();
    }

    /// <inheritdoc />
    public IDisposable Register(KeyboardShortcut shortcut, Action handler)
    {
        if (!shortcut.IsValid)
        {
            throw new ArgumentException("Invalid keyboard shortcut.", nameof(shortcut));
        }

        HandlerGroup group;
        lock (_stateGate)
        {
            if (!_keyboardRegistrations.TryGetValue(shortcut, out var existingGroup))
            {
                group = new HandlerGroup(NextRegistrationIdLocked());
                _keyboardRegistrations.Add(shortcut, group);
                _registrationsById.Add(group.RegistrationId, group);
            }
            else
            {
                group = existingGroup;
            }

            group.Handlers.Add(handler);
        }

        SignalStateChanged();
        return new LocalRegistration(this, group, handler, shortcut, null);
    }

    /// <inheritdoc />
    public IDisposable Register(MouseShortcut shortcut, Action handler)
    {
        HandlerGroup group;
        lock (_stateGate)
        {
            if (!_mouseRegistrations.TryGetValue(shortcut, out var existingGroup))
            {
                group = new HandlerGroup(NextRegistrationIdLocked());
                _mouseRegistrations.Add(shortcut, group);
                _registrationsById.Add(group.RegistrationId, group);
            }
            else
            {
                group = existingGroup;
            }

            group.Handlers.Add(handler);
        }

        SignalStateChanged();
        return new LocalRegistration(this, group, handler, null, shortcut);
    }

    /// <inheritdoc />
    public IKeyboardShortcutScope StartCaptureKeyboardShortcut()
    {
        RemoteKeyboardShortcutScope scope;
        lock (_stateGate)
        {
            if (_captureScope is { IsDisposedLocked: false })
            {
                return _captureScope;
            }

            scope = new RemoteKeyboardShortcutScope(this, NextCaptureIdLocked());
            _captureScope = scope;
        }

        SignalStateChanged();
        return scope;
    }

    /// <summary>Stops connection observation and releases all Main-side callback state.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!IsDisposed.FlipIfFalse())
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _stateChanges.Writer.TryComplete();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }

        lock (_stateGate)
        {
            _captureScope?.MarkDisposedLocked();
            _captureScope = null;
            _keyboardRegistrations.Clear();
            _mouseRegistrations.Clear();
            _registrationsById.Clear();
        }

        _callbacks.Writer.TryComplete();
        await _callbackTask.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var connection in _connectionSource.WatchConnectionsAsync(ProcessRole.Input, _lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await RunConnectionAsync(connection, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "The Input Host state connection ended unexpectedly.");
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RunConnectionAsync(RpcConnection connection, CancellationToken cancellationToken)
    {
        InputHostNotificationRpcBinding.Bind(connection, new ConnectionNotificationSink(this, connection), shouldPreserveOrder: true);
        var client = new InputHostRpcClient(connection);

        lock (_stateGate)
        {
            _activeConnection = connection;
            _lastSequence = 0;
        }

        while (_stateChanges.Reader.TryRead(out _))
        {
        }

        try
        {
            await ApplyStateAsync(client, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var stateWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var stateChanged = _stateChanges.Reader.ReadAsync(stateWait.Token).AsTask();
                var completed = await Task.WhenAny(connection.Completion, stateChanged).ConfigureAwait(false);
                if (completed == connection.Completion)
                {
                    await stateWait.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        await stateChanged.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stateWait.IsCancellationRequested)
                    {
                    }

                    await connection.Completion.ConfigureAwait(false);
                    return;
                }

                await stateChanged.ConfigureAwait(false);
                while (_stateChanges.Reader.TryRead(out _))
                {
                }
                await ApplyStateAsync(client, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CancelConnectionState(connection);
        }
    }

    private async Task ApplyStateAsync(InputHostRpcClient client, CancellationToken cancellationToken)
    {
        ApplyInputStateRequest request;
        lock (_stateGate)
        {
            request = CreateStateSnapshotLocked();
        }

        var response = await client.ApplyStateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsApplied)
        {
            throw new InvalidOperationException("Input Host did not apply Main's complete desired-state snapshot.");
        }
    }

    private ApplyInputStateRequest CreateStateSnapshotLocked() => new()
    {
        KeyboardRegistrations = _keyboardRegistrations
            .AsValueEnumerable()
            .Select(pair => new InputKeyboardRegistration
            {
                RegistrationId = pair.Value.RegistrationId,
                Key = (int)pair.Key.Key,
                Modifiers = (int)pair.Key.Modifiers
            })
            .ToArray(),
        MouseRegistrations = _mouseRegistrations
            .AsValueEnumerable()
            .Select(pair => new InputMouseRegistration
            {
                RegistrationId = pair.Value.RegistrationId,
                Button = (int)pair.Key.Key,
                DelayTicks = pair.Key.Delay.Ticks
            })
            .ToArray(),
        CaptureId = _captureScope is { IsDisposedLocked: false } ? _captureScope.CaptureId : 0
    };

    private async Task RunCallbacksAsync()
    {
        await foreach (var callback in _callbacks.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                callback();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "An Input Host callback failed.");
            }
        }
    }

    private void HandleShortcutTriggered(RpcConnection connection, ShortcutTriggeredNotification notification)
    {
        lock (_stateGate)
        {
            if (!AcceptNotificationLocked(connection, notification.Sequence, notification.UtcTicks) ||
                !_registrationsById.TryGetValue(notification.RegistrationId, out var registration))
            {
                return;
            }

            var handlers = registration.Handlers.ToArray();
            // All callbacks enter the same FIFO before releasing the state lock.
            // User code runs outside this lock and never on the transport reader.
            _callbacks.Writer.TryWrite(() =>
            {
                foreach (var handler in handlers)
                {
                    try
                    {
                        handler();
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "An Input Host shortcut callback failed.");
                    }
                }
            });
        }
    }

    private void HandleCaptureChanged(RpcConnection connection, ShortcutCaptureChangedNotification notification)
    {
        var shortcut = new KeyboardShortcut((Key)notification.Key, (KeyModifiers)notification.Modifiers);
        lock (_stateGate)
        {
            if (!AcceptNotificationLocked(connection, notification.Sequence, notification.UtcTicks) ||
                _captureScope is not { IsDisposedLocked: false } current ||
                current.CaptureId != notification.CaptureId)
            {
                return;
            }

            current.PressingShortcutLocked = shortcut;
            current.PublishChanged(shortcut);
        }
    }

    private void HandleCaptureFinished(RpcConnection connection, ShortcutCaptureFinishedNotification notification)
    {
        var shortcut = new KeyboardShortcut((Key)notification.Key, (KeyModifiers)notification.Modifiers);
        lock (_stateGate)
        {
            if (!AcceptNotificationLocked(connection, notification.Sequence, notification.UtcTicks) ||
                _captureScope is not { IsDisposedLocked: false } current ||
                current.CaptureId != notification.CaptureId)
            {
                return;
            }

            current.PressingShortcutLocked = shortcut;
            _captureScope = null;
            current.PublishFinished(shortcut);
        }

        SignalStateChanged();
    }

    private bool AcceptNotificationLocked(RpcConnection connection, ulong sequence, long utcTicks)
    {
        if (!ReferenceEquals(_activeConnection, connection) || sequence <= _lastSequence)
        {
            return false;
        }

        // Sequence records receive order even when the event itself is stale or
        // no longer names live state. A later lower sequence must not become valid.
        _lastSequence = sequence;
        var ageTicks = DateTimeOffset.UtcNow.UtcTicks - utcTicks;
        if (ageTicks > EventStaleThreshold.Ticks)
        {
            return false;
        }

        return true;
    }

    private void CancelConnectionState(RpcConnection connection)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_activeConnection, connection))
            {
                return;
            }

            _activeConnection = null;
            _lastSequence = 0;
            // Desired capture state belongs to this active connection even before
            // ApplyState is acknowledged. Disconnect must also cancel that admission.
            if (_captureScope is { } cancelledScope)
            {
                cancelledScope.PressingShortcutLocked = default;
                _captureScope = null;
                cancelledScope.PublishChanged(default);
                cancelledScope.PublishFinished(default);
            }
        }
    }

    private void ReleaseRegistration(
        HandlerGroup group,
        Action handler,
        KeyboardShortcut? keyboardShortcut,
        MouseShortcut? mouseShortcut)
    {
        var stateChanged = false;
        lock (_stateGate)
        {
            group.Handlers.Remove(handler);
            if (group.Handlers.Count == 0)
            {
                if (keyboardShortcut is not null)
                {
                    _keyboardRegistrations.Remove(keyboardShortcut.Value);
                }
                else if (mouseShortcut is not null)
                {
                    _mouseRegistrations.Remove(mouseShortcut.Value);
                }

                _registrationsById.Remove(group.RegistrationId);
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            SignalStateChanged();
        }
    }

    private void ReleaseCapture(RemoteKeyboardShortcutScope scope)
    {
        var stateChanged = false;
        lock (_stateGate)
        {
            scope.MarkDisposedLocked();
            if (ReferenceEquals(_captureScope, scope))
            {
                _captureScope = null;
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            SignalStateChanged();
        }
    }

    private ulong NextRegistrationIdLocked()
    {
        do
        {
            _nextRegistrationId++;
        }
        while (_nextRegistrationId == 0);

        return _nextRegistrationId;
    }

    private ulong NextCaptureIdLocked()
    {
        do
        {
            _nextCaptureId++;
        }
        while (_nextCaptureId == 0);

        return _nextCaptureId;
    }

    private void SignalStateChanged() => _stateChanges.Writer.TryWrite(0);

    private sealed class HandlerGroup(ulong registrationId)
    {
        public ulong RegistrationId { get; } = registrationId;

        public List<Action> Handlers { get; } = [];
    }

    private sealed class LocalRegistration(
        InputHostShortcutListener owner,
        HandlerGroup group,
        Action handler,
        KeyboardShortcut? keyboardShortcut,
        MouseShortcut? mouseShortcut) : IDisposable
    {
        private AtomicBoolean IsDisposed => new(ref _disposed);

        private int _disposed;

        public void Dispose()
        {
            if (IsDisposed.FlipIfFalse())
            {
                owner.ReleaseRegistration(group, handler, keyboardShortcut, mouseShortcut);
            }
        }
    }

    private sealed class RemoteKeyboardShortcutScope(InputHostShortcutListener owner, ulong captureId) : IKeyboardShortcutScope
    {
        public KeyboardShortcut PressingShortcut
        {
            get
            {
                lock (owner._stateGate)
                {
                    return PressingShortcutLocked;
                }
            }
        }

        public bool IsDisposed
        {
            get
            {
                lock (owner._stateGate)
                {
                    return IsDisposedLocked;
                }
            }
        }

        public event IKeyboardShortcutScope.PressingShortcutChangedHandler? PressingShortcutChanged;

        public event IKeyboardShortcutScope.ShortcutFinishedHandler? ShortcutFinished;

        public ulong CaptureId { get; } = captureId;

        public bool IsDisposedLocked { get; private set; }

        public KeyboardShortcut PressingShortcutLocked { get; set; }

        public void Dispose() => owner.ReleaseCapture(this);

        public void MarkDisposedLocked() => IsDisposedLocked = true;

        public void PublishChanged(KeyboardShortcut shortcut)
        {
            QueueNotification(() => PressingShortcutChanged?.Invoke(this, shortcut));
        }

        public void PublishFinished(KeyboardShortcut shortcut)
        {
            QueueNotification(() => ShortcutFinished?.Invoke(this, shortcut));
        }

        private void QueueNotification(Action notification)
        {
            owner._callbacks.Writer.TryWrite(() =>
            {
                if (!IsDisposed)
                {
                    notification();
                }
            });
        }
    }

    private sealed class ConnectionNotificationSink(InputHostShortcutListener owner, RpcConnection connection) : IInputHostNotificationRpc
    {
        public ValueTask ShortcutTriggeredAsync(ShortcutTriggeredNotification notification, CancellationToken cancellationToken = default)
        {
            owner.HandleShortcutTriggered(connection, notification);
            return ValueTask.CompletedTask;
        }

        public ValueTask CaptureChangedAsync(ShortcutCaptureChangedNotification notification, CancellationToken cancellationToken = default)
        {
            owner.HandleCaptureChanged(connection, notification);
            return ValueTask.CompletedTask;
        }

        public ValueTask CaptureFinishedAsync(ShortcutCaptureFinishedNotification notification, CancellationToken cancellationToken = default)
        {
            owner.HandleCaptureFinished(connection, notification);
            return ValueTask.CompletedTask;
        }
    }
}