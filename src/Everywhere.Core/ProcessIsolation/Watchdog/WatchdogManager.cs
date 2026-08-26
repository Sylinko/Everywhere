using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Everywhere.Common;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
#if WINDOWS
using Microsoft.Win32.SafeHandles;
#endif

namespace Everywhere.ProcessIsolation.Watchdog;

/// <summary>
/// Owns the application-wide Watchdog connection and the Main-side process
/// identities retained by active registrations. Startup is scheduled during the
/// initializer phase but never blocks the UI startup barrier; each RPC awaits the
/// same stored task before sending.
/// </summary>
public sealed partial class WatchdogManager : IWatchdogManager, IAsyncInitializer, IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    AsyncInitializerIndex IAsyncInitializer.Index => AsyncInitializerIndex.Startup;

    private AtomicBoolean IsDisposed => new(ref _isDisposed);
    private AtomicBoolean IsInitialized => new(ref _isInitialized);

    private readonly HashSet<WatchdogRegistration> _registrations = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WatchdogManager> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private int _isDisposed;
    private int _isInitialized;
    private Task<WatchdogSession?>? _sessionTask;

    /// <summary>Creates the singleton coordinator; <see cref="InitializeAsync"/> schedules its process startup.</summary>
    public WatchdogManager(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WatchdogManager>();
    }

    /// <summary>
    /// Proactively schedules Watchdog startup on a worker thread. Returning a
    /// completed task is deliberate: Watchdog readiness must not delay Everywhere's UI.
    /// </summary>
    public Task InitializeAsync()
    {
        if (IsInitialized.FlipIfFalse())
        {
            _sessionTask = Task.Run(() => StartSessionAsync(_lifetime.Token));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WatchdogRegistration?> RegisterProcessAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return RegisterProcessAsync(process);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            LogProcessExitedBeforeWatchdogCouldCaptureIt(processId, exception);
            return Task.FromResult<WatchdogRegistration?>(null);
        }
    }

    /// <inheritdoc />
    public Task<WatchdogRegistration?> RegisterProcessAsync(Process process)
    {
        try
        {
            // Capture before the first await. On Windows this owns a SafeHandle ref-count
            // lease, not another native handle; DuplicateHandle occurs later exactly once.
            var lease = SourceProcessLease.Capture(process);
            return RegisterCoreAsync(lease);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            LogProcessExitedBeforeWatchdogCouldCaptureIt(exception);
            return Task.FromResult<WatchdogRegistration?>(null);
        }
    }

    /// <summary>Stops the Watchdog connection and releases all Main-side process identities.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!IsDisposed.FlipIfFalse())
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var registration in _registrations)
            {
                registration.ReleaseSourceProcessLease();
            }
            _registrations.Clear();

            if (_sessionTask is not null)
            {
                var session = await _sessionTask.ConfigureAwait(false);
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _mutex.Release();
            _lifetime.Dispose();
        }
    }

    internal async Task ReleaseAsync(WatchdogRegistration registration, bool killIfRunning)
    {
        if (IsDisposed)
        {
            registration.ReleaseSourceProcessLease();
            return;
        }

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_registrations.Remove(registration))
            {
                return;
            }

            if (registration.RemoteHandle != 0)
            {
                await InvokeWithRecoveryAsync((client, cancellationToken) => client.UnregisterProcessAsync(
                    new UnregisterWatchdogProcessRequest
                    {
                        RegistrationHandle = registration.RemoteHandle,
                        KillIfRunning = killIfRunning
                    },
                    cancellationToken).AsTask()).ConfigureAwait(false);
            }
        }
        finally
        {
            registration.ReleaseSourceProcessLease();
            _mutex.Release();
        }
    }

    private async Task<WatchdogRegistration?> RegisterCoreAsync(SourceProcessLease lease)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            // ReSharper disable once AccessToDisposedClosure
            var response = await InvokeWithRecoveryAsync((client, cancellationToken) =>
                client.RegisterProcessAsync(lease.Request, cancellationToken).AsTask()).ConfigureAwait(false);
            if (response is not { Registered: true, RegistrationHandle: not 0 })
            {
                lease.Dispose();
                return null;
            }

            var registration = new WatchdogRegistration(this, lease, lease.Request, response.RegistrationHandle);
            _registrations.Add(registration);
            return registration;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<TResponse?> InvokeWithRecoveryAsync<TResponse>(Func<WatchdogRpcClient, CancellationToken, Task<TResponse>> invoke)
        where TResponse : class
    {
        var session = await GetScheduledSessionAsync().ConfigureAwait(false);
        if (session is null)
        {
            _sessionTask = Task.Run(() => StartSessionAsync(_lifetime.Token));
            session = await _sessionTask.ConfigureAwait(false);
            if (session is null)
            {
                return null;
            }

            try
            {
                await ReplayRegistrationsAsync(session.Client).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Active Watchdog registrations could not be restored after startup retry.");
                return null;
            }
        }

        try
        {
            return await invoke(session.Client, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (RpcRemoteException exception)
        {
            _logger.LogError(exception, "Watchdog rejected an application-owned process operation.");
            return null;
        }
        catch (Exception exception) when (exception is not RpcRemoteException)
        {
            _logger.LogWarning(exception, "The Watchdog connection failed. Starting one replacement session.");
        }

        await session.DisposeAsync().ConfigureAwait(false);
        _sessionTask = Task.Run(() => StartSessionAsync(_lifetime.Token));
        session = await _sessionTask.ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        try
        {
            await ReplayRegistrationsAsync(session.Client).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Active Watchdog registrations could not be restored after restart.");
            return null;
        }

        try
        {
            return await invoke(session.Client, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            _logger.LogError(exception, "The Watchdog RPC failed after one recovery attempt.");
            return null;
        }
    }

    private async Task ReplayRegistrationsAsync(WatchdogRpcClient client)
    {
        List<WatchdogRegistration>? expired = null;
        foreach (var registration in _registrations)
        {
            var response = await client.RegisterProcessAsync(registration.Request, _lifetime.Token).ConfigureAwait(false);
            if (response is { Registered: true, RegistrationHandle: not 0 })
            {
                registration.RemoteHandle = response.RegistrationHandle;
                continue;
            }

            (expired ??= []).Add(registration);
        }

        if (expired is null)
        {
            return;
        }

        foreach (var registration in expired)
        {
            _registrations.Remove(registration);
            registration.RemoteHandle = 0;
            registration.ReleaseSourceProcessLease();
        }
    }

    private Task<WatchdogSession?> GetScheduledSessionAsync() =>
        _sessionTask ?? throw new InvalidOperationException("Watchdog startup has not been scheduled by application initialization.");

    private async Task<WatchdogSession?> StartSessionAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream? server = null;
        Process? process = null;
        RpcConnection? connection = null;
        try
        {
            var pipeName = $"Everywhere.Watchdog-{RandomNumberGenerator.GetHexString(8)}";
            server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            var startInfo = new ProcessStartInfo
            {
                FileName = GetWatchdogPath(),
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add(pipeName);

            LogLaunchingWatchdogProcessWithPipeName(pipeName);
            process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("The Watchdog process could not be started.");
            }
            AttachOutputLogging(process);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(StartupTimeout);
            await server.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);

            connection = new RpcConnection(server, isServer: false);
            server = null;
            connection.Start(cancellationToken);

            var localIdentity = RpcRuntimeIdentity.CreateCurrent(ProcessRole.Main);
            var response = await connection.PerformHandshakeAsync(
                new RpcHandshake
                {
                    AssemblyInformationalVersion = localIdentity.AssemblyInformationalVersion,
                    Role = localIdentity.WireName,
                    ProcessId = localIdentity.ProcessId,
                    DesktopSessionId = localIdentity.DesktopSessionId
                },
                deadline.Token).ConfigureAwait(false);
            RpcHandshakeValidator.ValidateAcceptedPeer(response, RpcPeerNames.Watchdog, localIdentity);

            LogWatchdogProcessConnected(process.Id);
            return new WatchdogSession(connection, process);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Watchdog startup failed. Everywhere will continue without process monitoring.");
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
        if (process is not null)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            process.Dispose();
        }

        return null;
    }

    private void AttachOutputLogging(Process process)
    {
        var watchdogLogger = _loggerFactory.CreateLogger("Watchdog");
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (watchdogLogger.IsEnabled(LogLevel.Debug) && !string.IsNullOrEmpty(eventArgs.Data))
            {
                watchdogLogger.LogDebug("{Message}", eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrEmpty(eventArgs.Data))
            {
                watchdogLogger.LogError("{Message}", eventArgs.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static string GetWatchdogPath()
    {
#if MACOS
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../Helpers/Everywhere.Watchdog"));
#elif WINDOWS
        return Path.Combine(AppContext.BaseDirectory, "Everywhere.Watchdog.exe");
#else
        return Path.Combine(AppContext.BaseDirectory, "Everywhere.Watchdog");
#endif
    }

    /// <summary>
    /// Main-owned process identity retained for the full registration lifetime so
    /// a replacement Watchdog can recapture the same process without a PID lookup.
    /// </summary>
    private sealed class SourceProcessLease : IDisposable
    {
        public RegisterWatchdogProcessRequest Request { get; }

#if WINDOWS
        private SafeProcessHandle? _processHandle;
#endif

        private SourceProcessLease(
            RegisterWatchdogProcessRequest request
#if WINDOWS
            ,
            SafeProcessHandle processHandle
#endif
        )
        {
            Request = request;
#if WINDOWS
            _processHandle = processHandle;
#endif
        }

        public static SourceProcessLease Capture(Process process)
        {
            var processId = process.Id;
#if WINDOWS
            var processHandle = process.SafeHandle;
            var addedReference = false;
            processHandle.DangerousAddRef(ref addedReference);
            return new SourceProcessLease(
                new RegisterWatchdogProcessRequest
                {
                    ProcessId = processId,
                    SourceProcessHandle = processHandle.DangerousGetHandle().ToInt64()
                },
                processHandle);
#else
            return new SourceProcessLease(
                new RegisterWatchdogProcessRequest
                {
                    ProcessId = processId,
                    ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks
                });
#endif
        }

        public void Dispose()
        {
#if WINDOWS
            var processHandle = Interlocked.Exchange(ref _processHandle, null);
            processHandle?.DangerousRelease();
#endif
        }
    }

    /// <summary>Owns one authenticated RPC connection and its Watchdog process.</summary>
    private sealed class WatchdogSession(RpcConnection connection, Process process) : IAsyncDisposable
    {
        public WatchdogRpcClient Client { get; } = new(connection);

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            try
            {
                await process.WaitForExitAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    [LoggerMessage(LogLevel.Debug, "Process {ProcessId} exited before Watchdog could capture it.")]
    partial void LogProcessExitedBeforeWatchdogCouldCaptureIt(int processId, Exception exception);

    [LoggerMessage(LogLevel.Debug, "Process exited before Watchdog could capture it.")]
    partial void LogProcessExitedBeforeWatchdogCouldCaptureIt(Exception exception);

    [LoggerMessage(LogLevel.Debug, "Launching Watchdog process with pipe name {PipeName}.")]
    partial void LogLaunchingWatchdogProcessWithPipeName(string pipeName);

    [LoggerMessage(LogLevel.Debug, "Watchdog process {ProcessId} connected.")]
    partial void LogWatchdogProcessConnected(int processId);
}

public static class WatchdogServiceCollectionExtensions
{
    public static IServiceCollection AddWatchdogManager(this IServiceCollection services)
    {
        services.AddSingleton<WatchdogManager>();
        services.AddSingleton<IWatchdogManager>(serviceProvider => serviceProvider.GetRequiredService<WatchdogManager>());
        services.AddTransient<IAsyncInitializer>(serviceProvider => serviceProvider.GetRequiredService<WatchdogManager>());
        return services;
    }
}