using System.Diagnostics;
using System.IO.Pipes;
using Everywhere.Common;
using Everywhere.ProcessIsolation.Hosts.Diagnostics;
using Everywhere.ProcessIsolation.Hosts.Lifecycle;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Utilities;
using Serilog;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Aggregate result returned after a Host generation has been stopped. A role
/// with no authenticated connection is considered confirmed because there is no
/// Host lifetime lease for that role to drain.
/// </summary>
public sealed record HostStopResult(
    bool InputHostAcknowledged,
    bool AutomationHostAcknowledged
)
{
    /// <summary>Whether both fixed Host roles have a confirmed stopped state.</summary>
    public bool Succeeded => InputHostAcknowledged && AutomationHostAcknowledged;

    /// <summary>Result used when no Host generation is currently owned.</summary>
    public static HostStopResult NoGeneration { get; } = new(true, true);
}

/// <summary>
/// Describes a Host role whose automatic recovery circuit has opened after
/// repeated failures.
/// </summary>
public sealed record HostRecoveryStoppedEventArgs(
    ProcessRole Role,
    int FailureCount,
    TimeSpan FailureWindow
);

public delegate void AutomaticRecoveryStoppedHandler(HostRecoveryStoppedEventArgs e);

/// <summary>Connection state shown for one isolated Host role.</summary>
public enum HostConnectionState
{
    /// <summary>The Host is being launched or recovered.</summary>
    Starting,

    /// <summary>The Host is connected with its requested capabilities.</summary>
    Connected,

    /// <summary>The Host is connected through a fallback with reduced capabilities.</summary>
    Degraded,

    /// <summary>The Host could not establish or retain a connection.</summary>
    Unavailable
}

/// <summary>Current user-facing state of one isolated Host role.</summary>
/// <param name="Role">Host role represented by this status.</param>
/// <param name="State">Current connection state.</param>
/// <param name="MessageKey">Optional localized explanation suitable for persistent UI.</param>
public sealed record HostRoleStatus(ProcessRole Role, HostConnectionState State, IDynamicLocaleKey? MessageKey = null);

/// <summary>Receives a new user-facing status for one isolated Host role.</summary>
public delegate void HostStatusChangedHandler(HostRoleStatus status);

/// <summary>
/// Owns Main's connection leases to the Input and Automation Hosts. Each start
/// creates a fresh Host generation. A generation can be stopped explicitly and
/// later replaced, while an unexpected disconnect is still recovered immediately
/// inside that generation and subject to the three-failures-in-five-minutes rule.
/// </summary>
public sealed class HostProcessCoordinator : IHostConnectionSource, IAsyncInitializer, IAsyncDisposable
{
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ControllerTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ControllerCoalescingWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    public AsyncInitializerIndex Index => AsyncInitializerIndex.HostProcesses;

    public event AutomaticRecoveryStoppedHandler? AutomaticRecoveryStopped;

    public event HostStatusChangedHandler? StatusChanged;

    private AtomicBoolean IsDisposed => new(ref _isDisposed);

    private readonly ILogger _logger = Log.ForContext<HostProcessCoordinator>();
    private readonly RpcHandshakeIdentity _mainIdentity = RpcRuntimeIdentity.CreateCurrent(ProcessRole.Main);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private readonly Lock _generationStateGate = new();
    private readonly Lock _disposeGate = new();
    private readonly Lock _statusGate = new();
    private readonly INamedPipePeerVerifier _peerVerifier;
    private readonly IHostsServiceModeManager? _serviceModeManager;
    private TaskCompletionSource _generationChanged = CreateStateChangeSource();
    private HostRoleStatus _inputStatus = new(ProcessRole.Input, HostConnectionState.Starting);
    private HostRoleStatus _automationStatus = new(ProcessRole.Automation, HostConnectionState.Starting);
    private HostGeneration? _generation;
    private Task? _disposeTask;
    private int _isDisposed;

    private HostProcessCoordinator(INamedPipePeerVerifier peerVerifier, IHostsServiceModeManager? serviceModeManager)
    {
        _peerVerifier = peerVerifier;
        _serviceModeManager = serviceModeManager;
    }

    /// <summary>
    /// Creates an unstarted coordinator. Main uses this form when it must publish
    /// the control endpoint before the first Host generation begins, closing the
    /// startup window in which an external stop could otherwise arrive too early.
    /// </summary>
    /// <param name="peerVerifier">Verifies the identity of each named-pipe peer.</param>
    /// <param name="serviceModeManager">Optional platform service-mode integration. Platforms without one launch Hosts directly.</param>
    public static HostProcessCoordinator Create(INamedPipePeerVerifier peerVerifier, IHostsServiceModeManager? serviceModeManager = null) =>
        new(peerVerifier, serviceModeManager);

    /// <summary>Returns the latest runtime status for one Host role.</summary>
    public HostRoleStatus GetStatus(ProcessRole role)
    {
        lock (_statusGate)
        {
            return role switch
            {
                ProcessRole.Input => _inputStatus,
                ProcessRole.Automation => _automationStatus,
                _ => throw new ArgumentException("Main does not have an isolated Host status.", nameof(role))
            };
        }
    }

    /// <summary>Starts the first Host generation through the application initialization pipeline.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken) => StartHostsAsync(cancellationToken);

    /// <summary>
    /// Returns the currently authenticated connection for a Host role, waiting
    /// for the next replacement in the current generation.
    /// </summary>
    public ValueTask<RpcConnection> GetConnectionAsync(ProcessRole role, CancellationToken cancellationToken = default)
    {
        HostGeneration generation;
        lock (_generationStateGate)
        {
            generation = _generation ?? throw new InvalidOperationException("No Host generation is currently running.");
        }

        return generation.GetConnectionAsync(role, cancellationToken);
    }

    async IAsyncEnumerable<RpcConnection> IHostConnectionSource.WatchConnectionsAsync(
        ProcessRole role,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        HostGeneration? previousGeneration = null;

        while (!lifetime.IsCancellationRequested)
        {
            HostGeneration generation;
            try
            {
                generation = await WaitForGenerationAfterAsync(previousGeneration, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                yield break;
            }

            previousGeneration = generation;
            RpcConnection? previousConnection = null;
            while (!lifetime.IsCancellationRequested)
            {
                RpcConnection connection;
                try
                {
                    connection = await generation
                        .GetNextConnectionAsync(role, previousConnection, lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    break;
                }

                previousConnection = connection;
                yield return connection;
            }
        }
    }

    /// <summary>
    /// Starts a generation when none is running. The operation is idempotent and
    /// publishes its generation under the same gate as stop/restart. The bounded
    /// connection wait happens after publication so stop can cancel startup.
    /// </summary>
    public async Task StartHostsAsync(CancellationToken cancellationToken = default)
    {
        Task startupTask;
        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var generation = GetGeneration() ?? CreateGenerationCore();
            startupTask = generation.Start();
        }
        finally
        {
            _generationGate.Release();
        }

        // Startup waiting is deliberately outside the lifecycle gate. An
        // external stop can take the published generation immediately, cancel
        // its supervisors, and receive a bounded response even while the first
        // connection attempt is still in progress.
        await startupTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests normal cooperative shutdown and removes the generation from the
    /// coordinator. The returned result is the explicit aggregate confirmation
    /// consumed by the Main-control RPC.
    /// </summary>
    public Task<HostStopResult> StopHostsAsync(CancellationToken cancellationToken = default) =>
        StopHostsCoreAsync(HostStopReason.Shutdown, cancellationToken);

    /// <summary>
    /// Stops the current generation and starts a new one. This is the lifecycle
    /// primitive used later when switching between ordinary and elevated Hosts.
    /// An unsuccessful stop never starts a replacement over a possibly orphaned
    /// generation.
    /// </summary>
    public async Task RestartHostsAsync(CancellationToken cancellationToken = default)
    {
        Task startupTask;
        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = TakeGeneration();
            if (current is not null)
            {
                var stopResult = await StopGenerationAsync(current, HostStopReason.Shutdown, cancellationToken).ConfigureAwait(false);
                if (!stopResult.Succeeded)
                {
                    throw new TimeoutException("The current Host generation did not stop before restart.");
                }
            }

            var endpointsGone = await EndpointPresenceProbe
                .WaitForRolesToDisappearAsync(_mainIdentity.DesktopSessionId, ShutdownTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!endpointsGone)
            {
                throw new TimeoutException("A Host endpoint remained present after the current generation stopped.");
            }

            var replacement = CreateGenerationCore();
            startupTask = replacement.Start();
        }
        finally
        {
            _generationGate.Release();
        }

        await startupTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests update draining and stops the current generation. Success requires
    /// both acknowledgments and disappearance of both owned role endpoints before
    /// the updater may replace files.
    /// </summary>
    public async Task PrepareForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var shutdownDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        shutdownDeadline.CancelAfter(ShutdownTimeout);
        var result = await StopHostsCoreAsync(HostStopReason.PrepareForUpdate, shutdownDeadline.Token).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new TimeoutException("One or more Hosts did not acknowledge update shutdown.");
        }

        var endpointsGone = await EndpointPresenceProbe
            .WaitForRolesToDisappearAsync(_mainIdentity.DesktopSessionId, ShutdownTimeout, shutdownDeadline.Token)
            .ConfigureAwait(false);
        if (!endpointsGone)
        {
            throw new TimeoutException("A Host endpoint remained present after update shutdown.");
        }
    }

    /// <summary>
    /// Idempotently stops the current generation and releases the coordinator's
    /// process-wide resources. Disposal is terminal; a later start requires a new
    /// coordinator instance.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private HostGeneration CreateGenerationCore()
    {
        var generation = new HostGeneration(this, _lifetime.Token);
        SetGeneration(generation);
        return generation;
    }

    private bool ShouldUseServiceMode() =>
        _serviceModeManager?.GetStatus().State is HostsServiceModeConfigurationState.CurrentExecutable;

    private async Task<HostStopResult> StopHostsCoreAsync(HostStopReason reason, CancellationToken cancellationToken)
    {
        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var generation = TakeGeneration();
            return generation is null ?
                HostStopResult.NoGeneration :
                await StopGenerationAsync(generation, reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _generationGate.Release();
        }
    }

    private async static Task<HostStopResult> StopGenerationAsync(
        HostGeneration generation,
        HostStopReason reason,
        CancellationToken cancellationToken)
    {
        try
        {
            return await generation.StopAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await generation.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _generationGate.WaitAsync().ConfigureAwait(false);

            HostGeneration? generation;
            try
            {
                IsDisposed.FlipIfFalse();
                generation = TakeGeneration();
            }
            finally
            {
                _generationGate.Release();
            }

            if (generation is not null)
            {
                using var shutdownDeadline = new CancellationTokenSource(ShutdownTimeout);
                var result = await StopGenerationAsync(generation, HostStopReason.Shutdown, shutdownDeadline.Token).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    _logger.Warning("Host shutdown did not receive confirmation from every role.");
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Host shutdown did not complete cooperatively.");
        }
        finally
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            _lifetime.Dispose();
            _generationGate.Dispose();
        }
    }

    private HostGeneration? GetGeneration()
    {
        lock (_generationStateGate)
        {
            return _generation;
        }
    }

    private HostGeneration? TakeGeneration()
    {
        TaskCompletionSource? changed = null;
        HostGeneration? generation;
        lock (_generationStateGate)
        {
            generation = _generation;
            _generation = null;
            if (generation is not null)
            {
                changed = PulseGenerationChangedLocked();
            }
        }

        changed?.TrySetResult();
        return generation;
    }

    private void SetGeneration(HostGeneration generation)
    {
        TaskCompletionSource changed;
        lock (_generationStateGate)
        {
            _generation = generation;
            changed = PulseGenerationChangedLocked();
        }

        changed.TrySetResult();
    }

    private async ValueTask<HostGeneration> WaitForGenerationAfterAsync(HostGeneration? previousGeneration, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_generationStateGate)
            {
                if (_generation is not null && !ReferenceEquals(_generation, previousGeneration))
                {
                    return _generation;
                }

                changed = _generationChanged.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Replaces the generation-change signal while holding <see cref="_generationStateGate"/>.</summary>
    private TaskCompletionSource PulseGenerationChangedLocked()
    {
        var changed = _generationChanged;
        _generationChanged = CreateStateChangeSource();
        return changed;
    }

    private static TaskCompletionSource CreateStateChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(HostProcessCoordinator));
        }
    }

    private void OnAutomaticRecoveryStopped(ProcessRole role, int failureCount, TimeSpan failureWindow) =>
        ReportAutomaticRecoveryStopped(role, failureCount, failureWindow);

    private void ReportAutomaticRecoveryStopped(ProcessRole role, int failureCount, TimeSpan failureWindow)
    {
        SetStatus(new HostRoleStatus(
            role,
            HostConnectionState.Unavailable,
            new DynamicLocaleKey(LocaleKey.HostsStatusControl_RecoveryStopped_ToolTip)));
        AutomaticRecoveryStopped?.Invoke(new HostRecoveryStoppedEventArgs(role, failureCount, failureWindow));
    }

    private void SetStatus(HostRoleStatus status)
    {
        lock (_statusGate)
        {
            if (status.Role is ProcessRole.Input)
            {
                _inputStatus = status;
            }
            else if (status.Role is ProcessRole.Automation)
            {
                _automationStatus = status;
            }
            else
            {
                throw new ArgumentException("Main does not have an isolated Host status.", nameof(status));
            }
        }

        StatusChanged?.Invoke(status);
    }

    /// <summary>Reason sent to the role lifecycle contract.</summary>
    private enum HostStopReason
    {
        Shutdown,
        PrepareForUpdate
    }

    /// <summary>Generation-local result of the requested platform launch route.</summary>
    private enum HostLaunchOutcome
    {
        Pending,
        Preferred,
        EquivalentFallback,
        LimitedFallback,
        Failed
    }

    /// <summary>
    /// One independently stoppable pair of role supervisors. It owns the controller
    /// coalescing state so a fresh generation cannot inherit a stale start task or
    /// a previous generation's crash history.
    /// </summary>
    private sealed class HostGeneration : IAsyncDisposable
    {
        private readonly HostProcessCoordinator _owner;
        private readonly CancellationTokenSource _lifetime;
        private readonly bool _isServiceModeRequested;
        private readonly RoleConnectionSupervisor _input;
        private readonly RoleConnectionSupervisor _automation;
        private readonly Lock _controllerGate = new();

        private Task? _controllerTask;
        private Task? _startupTask;
        private long _lastControllerStartTimestamp;
        private int _launchOutcome;

        public HostGeneration(HostProcessCoordinator owner, CancellationToken parentToken)
        {
            _owner = owner;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _isServiceModeRequested = owner.ShouldUseServiceMode();
            _launchOutcome = (int)(_isServiceModeRequested ? HostLaunchOutcome.Pending : HostLaunchOutcome.Preferred);
            _input = new RoleConnectionSupervisor(this, ProcessRole.Input, _lifetime.Token);
            _automation = new RoleConnectionSupervisor(this, ProcessRole.Automation, _lifetime.Token);
            ReportStarting(ProcessRole.Input);
            ReportStarting(ProcessRole.Automation);
        }

        /// <summary>
        /// Starts both supervisors once and returns the shared bounded initial-
        /// connection observation. The coordinator lifecycle gate serializes all
        /// calls to this method, so a second per-generation lock is unnecessary.
        /// </summary>
        public Task Start() => _startupTask ??= StartCoreAsync();

        private async Task StartCoreAsync()
        {
            _input.Start();
            _automation.Start();

            var initialConnections = await Task.WhenAll(
                    _input.WaitForInitialConnectionAsync(),
                    _automation.WaitForInitialConnectionAsync())
                .ConfigureAwait(false);

            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            if (!initialConnections[0])
            {
                _owner._logger.Warning("The Input Host was unavailable after the bounded startup interval.");
            }

            if (!initialConnections[1])
            {
                _owner._logger.Warning("The Automation Host was unavailable after the bounded startup interval.");
            }
        }

        public ValueTask<RpcConnection> GetConnectionAsync(ProcessRole role, CancellationToken cancellationToken) =>
            GetSupervisor(role).GetConnectionAsync(cancellationToken);

        public ValueTask<RpcConnection> GetNextConnectionAsync(
            ProcessRole role,
            RpcConnection? previousConnection,
            CancellationToken cancellationToken) =>
            GetSupervisor(role).GetNextConnectionAsync(previousConnection, cancellationToken);

        public Task<HostStopResult> StopAsync(HostStopReason reason, CancellationToken cancellationToken) =>
            StopCoreAsync(reason, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            // HostGeneration is private and is disposed only by StopGenerationAsync,
            // after the single cooperative stop sequence has been attempted.
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_input.Completion, _automation.Completion).ConfigureAwait(false);
            _input.Dispose();
            _automation.Dispose();
            _lifetime.Dispose();
        }

        private RoleConnectionSupervisor GetSupervisor(ProcessRole role) => role switch
        {
            ProcessRole.Input => _input,
            ProcessRole.Automation => _automation,
            ProcessRole.Main => throw new ArgumentException("Main does not have a Host connection supervisor.", nameof(role)),
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
        };

        private async Task<HostStopResult> StopCoreAsync(HostStopReason reason, CancellationToken cancellationToken)
        {
            var results = await Task.WhenAll(
                    _input.StopAsync(reason, cancellationToken),
                    _automation.StopAsync(reason, cancellationToken))
                .ConfigureAwait(false);
            return new HostStopResult(results[0], results[1]);
        }

        private void ReportStarting(ProcessRole role, IDynamicLocaleKey? messageKey = null) =>
            _owner.SetStatus(new HostRoleStatus(role, HostConnectionState.Starting, messageKey));

        private void ReportUnavailable(ProcessRole role, IDynamicLocaleKey? messageKey) =>
            _owner.SetStatus(new HostRoleStatus(role, HostConnectionState.Unavailable, messageKey));

        private void ReportConnected(ProcessRole role)
        {
            var launchOutcome = (HostLaunchOutcome)Volatile.Read(ref _launchOutcome);
            if (launchOutcome is HostLaunchOutcome.Pending)
            {
                ReportStarting(role);
                return;
            }

            var isDegraded = launchOutcome is HostLaunchOutcome.LimitedFallback;
            _owner.SetStatus(new HostRoleStatus(
                role,
                isDegraded ? HostConnectionState.Degraded : HostConnectionState.Connected));
        }

        private void SetLaunchOutcome(int exitCode)
        {
            var launchOutcome = exitCode switch
            {
                HostsControlExitCodes.Success => HostLaunchOutcome.Preferred,
                HostsControlExitCodes.EquivalentFallbackStarted => HostLaunchOutcome.EquivalentFallback,
                HostsControlExitCodes.LimitedFallbackStarted => HostLaunchOutcome.LimitedFallback,
                _ => HostLaunchOutcome.Failed
            };
            Volatile.Write(ref _launchOutcome, (int)launchOutcome);

            if (exitCode is not HostsControlExitCodes.Success and
                not HostsControlExitCodes.EquivalentFallbackStarted and
                not HostsControlExitCodes.LimitedFallbackStarted)
            {
                _input.ReportControllerUnavailable();
                _automation.ReportControllerUnavailable();
                return;
            }

            _input.RefreshStatus();
            _automation.RefreshStatus();
        }

        /// <summary>
        /// Coalesces concurrent recovery requests. The controller only creates
        /// candidates; authenticated pipes remain the health signal.
        /// </summary>
        private Task EnsureHostsStartedAsync(CancellationToken cancellationToken)
        {
            Task controllerTask;
            lock (_controllerGate)
            {
                var now = Environment.TickCount64;
                if (_controllerTask is { IsCompleted: false })
                {
                    controllerTask = _controllerTask;
                }
                else if (_lastControllerStartTimestamp != 0 && now - _lastControllerStartTimestamp < ControllerCoalescingWindow.TotalMilliseconds)
                {
                    controllerTask = Task.CompletedTask;
                }
                else
                {
                    _lastControllerStartTimestamp = now;
                    _controllerTask = RunControllerAsync(_lifetime.Token);
                    controllerTask = _controllerTask;
                }
            }

            return controllerTask.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Runs the fixed controller command. Process creation is diagnostic only;
        /// an authenticated role pipe is the health and ownership signal.
        /// </summary>
        private async Task RunControllerAsync(CancellationToken cancellationToken)
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                _owner._logger.Warning("Hosts Control could not run because the current executable path is unavailable.");
                SetLaunchOutcome(HostsControlExitCodes.Failure);
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add("--hosts-control");
                startInfo.ArgumentList.Add(_isServiceModeRequested ? "start" : "launch");

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    _owner._logger.Warning("Hosts Control process creation returned no process handle.");
                    SetLaunchOutcome(HostsControlExitCodes.Failure);
                    return;
                }

                using var outputLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var errorLines = new List<string>();
                var errorTask = ReadControllerErrorsAsync(process.StandardError, errorLines, outputLifetime.Token);

                try
                {
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(ControllerTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await outputLifetime.CancelAsync();
                    await errorTask.ConfigureAwait(false);
                    _owner._logger.Warning("Hosts Control did not exit within {ControllerTimeout}.", ControllerTimeout);
                    SetLaunchOutcome(HostsControlExitCodes.Unavailable);
                    return;
                }

                // Hosts can inherit this pipe. Controller exit, rather than EOF from
                // its long-lived children, must release Main to connect to them.
                await outputLifetime.CancelAsync();
                await errorTask.ConfigureAwait(false);
                var detail = string.Join(Environment.NewLine, errorLines).Trim();
                SetLaunchOutcome(process.ExitCode);
                if (process.ExitCode != 0)
                {
                    _owner._logger.Warning(
                        "Hosts Control exited with code {ExitCode}. Detail: {Detail}",
                        process.ExitCode,
                        string.IsNullOrWhiteSpace(detail) ? "none" : detail);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _owner._logger.Warning(exception, "Hosts Control could not be started.");
                SetLaunchOutcome(HostsControlExitCodes.Failure);
            }
        }

        private static async Task ReadControllerErrorsAsync(StreamReader reader, List<string> lines, CancellationToken cancellationToken)
        {
            try
            {
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    lines.Add(line);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        /// <summary>Attempts one pipe connection and validates the Host identity.</summary>
        private async Task<RpcConnection?> TryConnectAsync(ProcessRole role, CancellationToken cancellationToken)
        {
            var endpoint = ProcessRoleNames.GetDefaultEndpoint(role, _owner._mainIdentity.DesktopSessionId);
            var stream = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            RpcConnection? connection = null;

            try
            {
                await stream.ConnectAsync((int)ConnectAttemptTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                _owner._peerVerifier.VerifyServer(stream);
                connection = new RpcConnection(stream, isServer: false);
                HostDiagnosticsRpcBinding.Bind(connection, new HostDiagnosticsLogSink(role));
                connection.Start(cancellationToken);

                var response = await connection.PerformHandshakeAsync(
                        new RpcHandshake
                        {
                            AssemblyInformationalVersion = _owner._mainIdentity.AssemblyInformationalVersion,
                            Role = ProcessRoleNames.ToWireName(ProcessRole.Main),
                            ProcessId = _owner._mainIdentity.ProcessId,
                            DesktopSessionId = _owner._mainIdentity.DesktopSessionId
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                RpcHandshakeValidator.ValidateAcceptedPeer(response, role, _owner._mainIdentity);
                return connection;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                await DisposeConnectionAsync(connection, stream).ConfigureAwait(false);
                return null;
            }
            catch
            {
                await DisposeConnectionAsync(connection, stream).ConfigureAwait(false);
                throw;
            }
        }

        private static async Task DisposeConnectionAsync(RpcConnection? connection, NamedPipeClientStream stream)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Supervises one role independently. A generation stop is explicit and
        /// suppresses recovery; unexpected disconnects remain recoverable.
        /// </summary>
        private sealed class RoleConnectionSupervisor(HostGeneration generation, ProcessRole role, CancellationToken lifetime) : IDisposable
        {
            private static TimeSpan CrashTimeWindow => TimeSpan.FromMinutes(5);
            private const int CrashLimit = 3;

            public Task Completion => _runTask ?? Task.CompletedTask;

            private AtomicBoolean StopRequested => new(ref _stopRequested);

            private readonly ILogger _logger = Log.ForContext<RoleConnectionSupervisor>().ForContext(
                "ProcessRole",
                ProcessRoleNames.ToWireName(role));
            private readonly CancellationTokenSource _stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            private readonly Lock _connectionGate = new();
            private readonly Queue<long> _failures = new();
            private readonly TaskCompletionSource<bool> _initialConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private TaskCompletionSource<RpcConnection> _nextConnection = CreateConnectionSource();
            private RpcConnection? _connection;
            private Task? _runTask;
            private int _stopRequested;

            public void Start() => _runTask = RunAsync();

            public Task<bool> WaitForInitialConnectionAsync() => _initialConnection.Task;

            public ValueTask<RpcConnection> GetConnectionAsync(CancellationToken cancellationToken)
            {
                lock (_connectionGate)
                {
                    if (_connection is not null)
                    {
                        return ValueTask.FromResult(_connection);
                    }

                    return new ValueTask<RpcConnection>(_nextConnection.Task.WaitAsync(cancellationToken));
                }
            }

            public ValueTask<RpcConnection> GetNextConnectionAsync(
                RpcConnection? previousConnection,
                CancellationToken cancellationToken)
            {
                lock (_connectionGate)
                {
                    if (_connection is not null && !ReferenceEquals(_connection, previousConnection))
                    {
                        return ValueTask.FromResult(_connection);
                    }

                    return new ValueTask<RpcConnection>(_nextConnection.Task.WaitAsync(cancellationToken));
                }
            }

            public Task<bool> StopAsync(HostStopReason reason, CancellationToken cancellationToken) =>
                StopCoreAsync(reason, cancellationToken);

            public void Dispose() => _stopping.Dispose();

            private async Task<bool> StopCoreAsync(HostStopReason reason, CancellationToken cancellationToken)
            {
                // A generation owns exactly one stop sequence. This flag is read by
                // the connection loop so a connection racing with stop is discarded.
                StopRequested.FlipIfFalse();

                RpcConnection? connection;
                lock (_connectionGate)
                {
                    connection = _connection;
                }

                var acknowledged = connection is null;
                try
                {
                    if (connection is not null)
                    {
                        var client = new HostLifecycleRpcClient(connection);
                        var response = reason is HostStopReason.PrepareForUpdate ?
                            await client.PrepareForUpdateAsync(new PrepareForUpdateRequest(), cancellationToken).ConfigureAwait(false) :
                            await client.ShutdownAsync(new ShutdownRequest(), cancellationToken).ConfigureAwait(false);
                        acknowledged = response.Accepted || response.Reason == "already_draining";

                        try
                        {
                            await connection.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception) when (connection.Completion.IsCompleted)
                        {
                            // The lifecycle response was observed; transport EOF while
                            // the Host performs final cleanup does not revoke the ack.
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    acknowledged = connection?.Completion.IsCompleted ?? true;
                    _logger.Warning(exception, "The {Role} Host did not acknowledge cooperative shutdown.", ProcessRoleNames.ToWireName(role));
                }
                finally
                {
                    await _stopping.CancelAsync().ConfigureAwait(false);
                    if (connection is not null && !connection.Completion.IsCompleted)
                    {
                        try
                        {
                            await connection.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception) when (cancellationToken.IsCancellationRequested || connection.Completion.IsCompleted)
                        {
                        }
                    }

                    try
                    {
                        await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        acknowledged = false;
                    }
                }

                return acknowledged;
            }

            private async Task RunAsync()
            {
                try
                {
                    var isInitialAttempt = true;
                    while (!_stopping.IsCancellationRequested)
                    {
                        var connection = await ConnectWithinStartupWindowAsync(_stopping.Token).ConfigureAwait(false);
                        if (connection is null)
                        {
                            generation.ReportUnavailable(
                                role,
                                new DynamicLocaleKey(LocaleKey.HostsStatusControl_StartupTimedOut_ToolTip));
                            if (isInitialAttempt)
                            {
                                _initialConnection.TrySetResult(false);
                                isInitialAttempt = false;
                            }

                            if (_stopping.IsCancellationRequested)
                            {
                                break;
                            }

                            if (RecordFailureAndIsCircuitOpen())
                            {
                                ReportAutomaticRecoveryStopped();
                                break;
                            }

                            continue;
                        }

                        if (StopRequested)
                        {
                            await connection.DisposeAsync().ConfigureAwait(false);
                            break;
                        }

                        SetConnection(connection);
                        if (isInitialAttempt)
                        {
                            _initialConnection.TrySetResult(true);
                            isInitialAttempt = false;
                        }

                        try
                        {
                            await connection.Completion.ConfigureAwait(false);
                        }
                        catch (Exception exception) when (!_stopping.IsCancellationRequested)
                        {
                            _logger.Warning(exception, "The Host RPC connection ended unexpectedly.");
                        }
                        finally
                        {
                            ClearConnection(connection);
                            await connection.DisposeAsync().ConfigureAwait(false);
                        }

                        if (StopRequested || _stopping.IsCancellationRequested)
                        {
                            break;
                        }

                        if (RecordFailureAndIsCircuitOpen())
                        {
                            ReportAutomaticRecoveryStopped();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    _initialConnection.TrySetResult(false);
                    _logger.Error(exception, "The Host connection supervisor stopped unexpectedly.");
                }
                finally
                {
                    _initialConnection.TrySetResult(false);
                    _nextConnection.TrySetCanceled(_stopping.Token);
                }
            }

            private void ReportAutomaticRecoveryStopped()
            {
                _logger.Error(
                    "Automatic Host recovery stopped after {CrashLimit} failures within {CrashWindow}.",
                    CrashLimit,
                    CrashTimeWindow);
                generation._owner.OnAutomaticRecoveryStopped(role, CrashLimit, CrashTimeWindow);
            }

            private async Task<RpcConnection?> ConnectWithinStartupWindowAsync(CancellationToken cancellationToken)
            {
                try
                {
                    var existingConnection = await generation.TryConnectAsync(role, cancellationToken).ConfigureAwait(false);
                    if (existingConnection is not null)
                    {
                        return existingConnection;
                    }

                    await generation.EnsureHostsStartedAsync(cancellationToken).ConfigureAwait(false);
                    var startedAt = Environment.TickCount64;
                    while (Environment.TickCount64 - startedAt < StartupTimeout.TotalMilliseconds)
                    {
                        var connection = await generation.TryConnectAsync(role, cancellationToken).ConfigureAwait(false);
                        if (connection is not null)
                        {
                            return connection;
                        }

                        await Task.Delay(ConnectRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception exception)
                {
                    _logger.Warning(exception, "The Host connection attempt failed.");
                }

                return null;
            }

            private void SetConnection(RpcConnection connection)
            {
                TaskCompletionSource<RpcConnection> nextConnection;
                lock (_connectionGate)
                {
                    _connection = connection;
                    nextConnection = _nextConnection;
                    _nextConnection = CreateConnectionSource();
                }

                nextConnection.TrySetResult(connection);

                generation.ReportConnected(role);

                _logger.Information("Authenticated the Host RPC connection.");
            }

            private void ClearConnection(RpcConnection connection)
            {
                lock (_connectionGate)
                {
                    if (!ReferenceEquals(_connection, connection))
                    {
                        return;
                    }

                    _connection = null;
                }

                if (!StopRequested && !_stopping.IsCancellationRequested)
                {
                    generation.ReportStarting(
                        role,
                        new DynamicLocaleKey(LocaleKey.HostsStatusControl_Reconnecting_ToolTip));
                }
            }

            public void RefreshStatus()
            {
                lock (_connectionGate)
                {
                    if (_connection is not null)
                    {
                        generation.ReportConnected(role);
                    }
                }
            }

            public void ReportControllerUnavailable()
            {
                lock (_connectionGate)
                {
                    if (_connection is not null)
                    {
                        generation.ReportConnected(role);
                        return;
                    }
                }

                generation.ReportUnavailable(
                    role,
                    new DynamicLocaleKey(LocaleKey.HostsStatusControl_ServiceLaunchFailed_ToolTip));
            }

            private bool RecordFailureAndIsCircuitOpen()
            {
                var now = Environment.TickCount64;
                while (_failures.TryPeek(out var timestamp) && now - timestamp > CrashTimeWindow.TotalMilliseconds)
                {
                    _failures.Dequeue();
                }

                _failures.Enqueue(now);
                return _failures.Count >= CrashLimit;
            }

            private static TaskCompletionSource<RpcConnection> CreateConnectionSource() =>
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}