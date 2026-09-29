using System.ComponentModel;
using System.Threading.Channels;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Utilities;
using Microsoft.Extensions.Logging;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Owns Main's text-selection lifecycle and restores one monitor across Automation Host connections.</summary>
public sealed class AutomationTextSelectionWatcher : ObservableObject, ITextSelectionWatcher, IAsyncDisposable
{
    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            lock (_stateGate)
            {
                return _isCommittedEnabled;
            }
        }
    }

    /// <inheritdoc />
    public IAsyncRelayCommand<bool> SetEnabledCommand { get; }

    /// <inheritdoc />
    public event TextSelectionMonitoringRejectedHandler? RestorationRejected;

    private readonly IHostConnectionSource _connectionSource;
    private readonly ChatVisualService _visualService;
    private readonly Settings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger<AutomationTextSelectionWatcher> _logger;
    private readonly DebounceExecutor<AutomationTextSelectionWatcher, DispatcherTimerImpl> _configurationDebounce;
    private readonly Lock _deliveryGate = new();
    private readonly Channel<byte> _stateChanges = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly Task _runTask;

    private RpcConnection? _activeConnection;
    private long _appliedConfigurationRevision = -1;
    private TextSelectionMonitoringConfiguration _configuration = TextSelectionMonitoringConfiguration.CreateDefault();
    private long _configurationRevision;
    private RemoteVisualContext? _context;
    private long _contextId;
    private bool _isAccepting;
    private int _isDisposed;
    private bool _isCommittedEnabled;
    private bool _isEnabled;
    private bool _isRestored;
    private long _lastRevision;
    private long _lifecycleRevision;
    private long _monitorId;
    private RemoteTextSelectionMonitor? _monitor;
    private PendingTransition? _pendingTransition;
    private ObserverSubscription? _subscription;

    /// <summary>Starts observing Automation Host connections.</summary>
    internal AutomationTextSelectionWatcher(
        IHostConnectionSource connectionSource,
        ChatVisualService visualService,
        Settings settings,
        ILogger<AutomationTextSelectionWatcher> logger)
    {
        _connectionSource = connectionSource;
        _visualService = visualService;
        _settings = settings;
        _logger = logger;
        SetEnabledCommand = new AsyncRelayCommand<bool>(SetEnabledFromCommandAsync);
        _configurationDebounce = new DebounceExecutor<AutomationTextSelectionWatcher, DispatcherTimerImpl>(
            () => this,
            static watcher => watcher.UpdateConfiguration(watcher._settings.ChatWindow.CreateTextSelectionMonitoringConfiguration()),
            TimeSpan.FromMilliseconds(300));
        _runTask = RunAsync();
    }

    /// <inheritdoc />
    public IDisposable Subscribe(IObserver<TextSelectionData> observer)
    {
        var subscription = new ObserverSubscription(this, observer);
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
            if (_subscription is not null) throw new InvalidOperationException("Text-selection monitoring supports one owning result consumer.");
            _subscription = subscription;
        }
        SignalStateChanged();
        return subscription;
    }

    /// <inheritdoc />
    public void RestoreState(bool isEnabled, TextSelectionMonitoringConfiguration configuration)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
            _isEnabled = isEnabled;
            _isCommittedEnabled = isEnabled;
            _configuration = configuration;
            _lifecycleRevision++;
            _configurationRevision++;
            if (!isEnabled) _isAccepting = false;
        }
        if (!_isRestored)
        {
            _isRestored = true;
            _settings.ChatWindow.PropertyChanged += HandleSettingsChanged;
        }
        OnPropertyChanged(nameof(IsEnabled));
        SignalStateChanged();
    }

    private async ValueTask<TextSelectionMonitoringControlResult> ChangeEnabledCoreAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingTransition transition;
            var shouldCompleteWithoutConnection = false;
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
                if (_pendingTransition is not null) throw new InvalidOperationException("A text-selection lifecycle transition is already pending.");

                if (_isEnabled != isEnabled)
                {
                    _isEnabled = isEnabled;
                    _lifecycleRevision++;
                }
                if (!isEnabled) _isAccepting = false;

                transition = new PendingTransition(_lifecycleRevision, isEnabled);
                _pendingTransition = transition;
                if (!isEnabled && _activeConnection is null)
                {
                    _pendingTransition = null;
                    shouldCompleteWithoutConnection = true;
                }
            }

            SignalStateChanged();
            if (shouldCompleteWithoutConnection)
            {
                transition.Completion.TrySetResult(TextSelectionMonitoringControlResult.Success());
            }
            try
            {
                return await transition.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_pendingTransition, transition))
                    {
                        _pendingTransition = null;
                        if (isEnabled)
                        {
                            _isEnabled = false;
                            _isAccepting = false;
                            _lifecycleRevision++;
                        }
                    }
                }
                SignalStateChanged();
                throw;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <inheritdoc />
    public void UpdateConfiguration(TextSelectionMonitoringConfiguration configuration)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
            _configuration = configuration;
            _configurationRevision++;
        }
        SignalStateChanged();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        if (_isRestored)
        {
            _settings.ChatWindow.PropertyChanged -= HandleSettingsChanged;
            _isRestored = false;
        }
        _configurationDebounce.Dispose();
        ObserverSubscription? subscription;
        PendingTransition? transition;
        lock (_stateGate)
        {
            _isAccepting = false;
            _isEnabled = false;
            subscription = _subscription;
            _subscription = null;
            transition = _pendingTransition;
            _pendingTransition = null;
        }
        subscription?.Stop();
        transition?.Completion.TrySetCanceled();

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _stateChanges.Writer.TryComplete();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }

        _transitionGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task SetEnabledFromCommandAsync(bool isEnabled)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var result = await ChangeEnabledCoreAsync(isEnabled, timeout.Token);
            if (!result.IsSucceeded) throw new TextSelectionMonitoringRejectedException(result);
            CommitEnabledState(isEnabled);
        }
        catch
        {
            if (!isEnabled) CommitEnabledState(false);
            throw;
        }
    }

    private void HandleSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(ChatWindowSettings.IsTextSelectionFullscreenExcluded) or
            nameof(ChatWindowSettings.TextSelectionExcludedApplications))
        {
            _configurationDebounce.Trigger();
        }
    }

    private void CommitEnabledState(bool isEnabled)
    {
        lock (_stateGate)
        {
            if (_isCommittedEnabled == isEnabled && _settings.ChatWindow.AutomaticallyAddTextSelection == isEnabled) return;
            _isCommittedEnabled = isEnabled;
        }
        _settings.ChatWindow.AutomaticallyAddTextSelection = isEnabled;
        OnPropertyChanged(nameof(IsEnabled));
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var connection in _connectionSource.WatchConnectionsAsync(ProcessRole.Automation, _lifetime.Token).ConfigureAwait(false))
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
                    FailPendingTransition(exception);
                    _logger.LogWarning(exception, "The Automation Host text-selection connection ended unexpectedly.");
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
        AutomationHostNotificationRpcBinding.Bind(connection, new ConnectionNotificationSink(this, connection));
        lock (_stateGate)
        {
            _activeConnection = connection;
            _appliedConfigurationRevision = -1;
            _lastRevision = 0;
        }

        while (_stateChanges.Reader.TryRead(out _))
        {
        }

        try
        {
            await ApplyStateAsync(connection, cancellationToken).ConfigureAwait(false);
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
                await ApplyStateAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ClearConnectionState(connection);
        }
    }

    private async Task ApplyStateAsync(RpcConnection connection, CancellationToken cancellationToken)
    {
        bool isEnabled;
        TextSelectionMonitoringConfiguration configuration;
        long configurationRevision;
        long lifecycleRevision;
        RemoteTextSelectionMonitor? monitor;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_activeConnection, connection)) return;
            isEnabled = _isEnabled && _subscription is not null;
            configuration = _configuration;
            configurationRevision = _configurationRevision;
            lifecycleRevision = _lifecycleRevision;
            monitor = _monitor;
        }

        if (!isEnabled)
        {
            await StopRemoteStateAsync(connection, cancellationToken).ConfigureAwait(false);
            CompletePendingTransition(lifecycleRevision, false, TextSelectionMonitoringControlResult.Success());
            return;
        }

        if (monitor is null)
        {
            await StartRemoteStateAsync(
                connection,
                lifecycleRevision,
                configuration,
                configurationRevision,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_appliedConfigurationRevision != configurationRevision)
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_monitor, monitor)) _isAccepting = false;
            }
            await monitor.UpdateConfigurationAsync(configuration, cancellationToken).ConfigureAwait(false);
            var shouldReapply = false;
            lock (_stateGate)
            {
                if (ReferenceEquals(_activeConnection, connection) && ReferenceEquals(_monitor, monitor) && _isEnabled)
                {
                    _appliedConfigurationRevision = configurationRevision;
                    _isAccepting = _configurationRevision == configurationRevision;
                    shouldReapply = !_isAccepting;
                }
            }
            if (shouldReapply) SignalStateChanged();
        }

        CompletePendingTransition(lifecycleRevision, true, TextSelectionMonitoringControlResult.Success());
    }

    private async Task StartRemoteStateAsync(
        RpcConnection connection,
        long lifecycleRevision,
        TextSelectionMonitoringConfiguration configuration,
        long configurationRevision,
        CancellationToken cancellationToken)
    {
        var context = await _visualService.GetAcquisitionContextAsync(connection, cancellationToken).ConfigureAwait(false);
        var monitorId = connection.AllocateResourceId();
        long contextId;
        using (var contextLease = context.AcquireLease()) contextId = contextLease.ResourceId;

        var start = await context.StartTextSelectionMonitoringAsync(
            monitorId,
            Environment.ProcessId,
            configuration,
            cancellationToken).ConfigureAwait(false);
        if (!start.Result.IsSucceeded)
        {
            RejectEnable(lifecycleRevision, start.Result);
            return;
        }

        var monitor = start.Monitor ?? throw new InvalidDataException("A successful text-selection start did not return a monitor handle.");
        var shouldKeep = false;
        var shouldApplyLatestConfiguration = false;
        lock (_stateGate)
        {
            if (ReferenceEquals(_activeConnection, connection) && _lifecycleRevision == lifecycleRevision && _isEnabled && _subscription is not null)
            {
                _context = context;
                _contextId = contextId;
                _monitorId = monitorId;
                _lastRevision = 0;
                _monitor = monitor;
                _appliedConfigurationRevision = configurationRevision;
                _isAccepting = _configurationRevision == configurationRevision;
                shouldApplyLatestConfiguration = !_isAccepting;
                shouldKeep = true;
            }
        }

        if (!shouldKeep)
        {
            await monitor.StopAsync(cancellationToken).ConfigureAwait(false);
            monitor.Dispose();
            SignalStateChanged();
            return;
        }

        CompletePendingTransition(lifecycleRevision, true, TextSelectionMonitoringControlResult.Success());
        if (shouldApplyLatestConfiguration) SignalStateChanged();
    }

    private void RejectEnable(long lifecycleRevision, TextSelectionMonitoringControlResult result)
    {
        PendingTransition? transition = null;
        var shouldNotify = false;
        lock (_stateGate)
        {
            if (_lifecycleRevision != lifecycleRevision || !_isEnabled) return;

            _isEnabled = false;
            _isAccepting = false;
            _lifecycleRevision++;
            if (_pendingTransition is { TargetIsEnabled: true } pending && pending.LifecycleRevision == lifecycleRevision)
            {
                transition = pending;
                _pendingTransition = null;
            }
            else
            {
                shouldNotify = true;
            }
        }

        transition?.Completion.TrySetResult(result);
        if (shouldNotify)
        {
            Dispatcher.UIThread.PostOnDemand(() =>
            {
                CommitEnabledState(false);
                RestorationRejected?.Invoke(result);
            });
        }
        SignalStateChanged();
    }

    private void HandleTextSelectionObserved(RpcConnection connection, TextSelectionObservedNotification notification)
    {
        lock (_deliveryGate)
        {
            HandleTextSelectionObservedCore(connection, notification);
        }
    }

    private void HandleTextSelectionObservedCore(RpcConnection connection, TextSelectionObservedNotification notification)
    {
        RemoteVisualContext? context;
        lock (_stateGate)
        {
            if (!_isAccepting ||
                !ReferenceEquals(_activeConnection, connection) ||
                notification.ContextId != _contextId ||
                notification.MonitorId != _monitorId ||
                notification.Revision <= _lastRevision)
            {
                ReleasePushedAnchor(connection, notification.AnchorId);
                return;
            }

            context = _context;
        }

        RemoteVisualAnchor? anchor = null;
        var shouldReleaseAnchorId = notification.AnchorId != 0;
        try
        {
            if (string.IsNullOrEmpty(notification.Text) || (notification.AnchorId == 0) != (notification.Source is null))
            {
                ReleasePushedAnchor(connection, notification.AnchorId);
                return;
            }

            if (notification.AnchorId != 0)
            {
                if (notification.AnchorId >= 0 || notification.Source is null || context is null)
                {
                    ReleasePushedAnchor(connection, notification.AnchorId);
                    return;
                }
                anchor = context.CreatePushedAnchor(notification.AnchorId, notification.Source);
                shouldReleaseAnchorId = false;
            }

            ObserverSubscription? subscription;
            lock (_stateGate)
            {
                if (!_isAccepting ||
                    !ReferenceEquals(_activeConnection, connection) ||
                    notification.ContextId != _contextId ||
                    notification.MonitorId != _monitorId ||
                    notification.Revision <= _lastRevision)
                {
                    anchor?.Dispose();
                    return;
                }

                _lastRevision = notification.Revision;
                subscription = _subscription;
            }
            if (subscription is null)
            {
                anchor?.Dispose();
                return;
            }

            var revision = notification.Revision;
            TextSelectionData? data = new(
                notification.Text,
                notification.IsTextIncomplete,
                anchor,
                () => IsDeliveryCurrent(connection, notification.MonitorId, revision, subscription));
            anchor = null;
            try
            {
                if (subscription.TryPublish(data)) data = null;
            }
            finally
            {
                data?.Dispose();
            }
        }
        catch (Exception exception)
        {
            anchor?.Dispose();
            if (shouldReleaseAnchorId) ReleasePushedAnchor(connection, notification.AnchorId);
            _logger.LogWarning(exception, "Failed to accept an Automation Host text-selection result.");
        }
    }

    private bool IsDeliveryCurrent(
        RpcConnection connection,
        long monitorId,
        long revision,
        ObserverSubscription subscription)
    {
        lock (_stateGate)
        {
            return _isAccepting &&
                ReferenceEquals(_activeConnection, connection) &&
                _monitorId == monitorId &&
                _lastRevision == revision &&
                ReferenceEquals(_subscription, subscription);
        }
    }

    private async Task StopRemoteStateAsync(RpcConnection connection, CancellationToken cancellationToken)
    {
        RemoteTextSelectionMonitor? monitor;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_activeConnection, connection)) return;
            _isAccepting = false;
            monitor = _monitor;
        }

        if (monitor is not null) await monitor.StopAsync(cancellationToken).ConfigureAwait(false);

        lock (_stateGate)
        {
            if (!ReferenceEquals(_activeConnection, connection) || !ReferenceEquals(_monitor, monitor)) return;
            _monitor = null;
            _context = null;
            _contextId = 0;
            _monitorId = 0;
            _lastRevision = 0;
            _appliedConfigurationRevision = -1;
        }
        monitor?.Dispose();
    }

    private void ClearConnectionState(RpcConnection connection)
    {
        RemoteTextSelectionMonitor? monitor;
        PendingTransition? disableTransition = null;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_activeConnection, connection)) return;
            _isAccepting = false;
            monitor = _monitor;
            _monitor = null;
            _context = null;
            _contextId = 0;
            _monitorId = 0;
            _lastRevision = 0;
            _appliedConfigurationRevision = -1;
            _activeConnection = null;
            if (_pendingTransition is { TargetIsEnabled: false } pending)
            {
                disableTransition = pending;
                _pendingTransition = null;
            }
        }
        monitor?.Dispose();
        disableTransition?.Completion.TrySetResult(TextSelectionMonitoringControlResult.Success());
    }

    private static void ReleasePushedAnchor(RpcConnection connection, long anchorId)
    {
        if (anchorId >= 0) return;
        try
        {
            connection.GetSafeHandleReleaseQueue().TryQueueRelease(anchorId);
        }
        catch (ObjectDisposedException)
        {
            // Connection teardown owns every remaining Host-side registration.
        }
    }

    private void CompletePendingTransition(
        long lifecycleRevision,
        bool targetIsEnabled,
        TextSelectionMonitoringControlResult result)
    {
        PendingTransition? transition = null;
        lock (_stateGate)
        {
            if (_pendingTransition is { } pending &&
                pending.LifecycleRevision == lifecycleRevision &&
                pending.TargetIsEnabled == targetIsEnabled)
            {
                transition = pending;
                _pendingTransition = null;
            }
        }
        transition?.Completion.TrySetResult(result);
    }

    private void FailPendingTransition(Exception exception)
    {
        PendingTransition? transition;
        lock (_stateGate)
        {
            transition = _pendingTransition;
            _pendingTransition = null;
            if (transition?.TargetIsEnabled == true)
            {
                _isEnabled = false;
                _isAccepting = false;
                _lifecycleRevision++;
            }
        }
        transition?.Completion.TrySetException(exception);
    }

    private void Unsubscribe(ObserverSubscription subscription)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_subscription, subscription)) return;
            _subscription = null;
            _isAccepting = false;
            _isEnabled = false;
            _lifecycleRevision++;
        }
        subscription.Stop();
        SignalStateChanged();
    }

    private void SignalStateChanged() => _stateChanges.Writer.TryWrite(0);

    private sealed class PendingTransition(long lifecycleRevision, bool targetIsEnabled)
    {
        public long LifecycleRevision { get; } = lifecycleRevision;
        public bool TargetIsEnabled { get; } = targetIsEnabled;

        public TaskCompletionSource<TextSelectionMonitoringControlResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ObserverSubscription(
        AutomationTextSelectionWatcher owner,
        IObserver<TextSelectionData> observer
    ) : IDisposable
    {
        private readonly Lock _gate = new();
        private IObserver<TextSelectionData>? _observer = observer;

        public bool TryPublish(TextSelectionData data)
        {
            lock (_gate)
            {
                if (_observer is null) return false;
                _observer.OnNext(data);
                return true;
            }
        }

        public void Dispose() => owner.Unsubscribe(this);

        public void Stop()
        {
            lock (_gate)
            {
                _observer = null;
            }
        }
    }

    private sealed class ConnectionNotificationSink(AutomationTextSelectionWatcher owner, RpcConnection connection) : IAutomationHostNotificationRpc
    {
        public ValueTask TextSelectionObservedAsync(
            TextSelectionObservedNotification notification,
            CancellationToken cancellationToken = default)
        {
            owner.HandleTextSelectionObserved(connection, notification);
            return ValueTask.CompletedTask;
        }
    }
}