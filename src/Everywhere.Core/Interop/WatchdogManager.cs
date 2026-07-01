using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Everywhere.Common;
using Everywhere.Rpc;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Interop;

/// <summary>
/// Manages the lifecycle and communication with the Watchdog process.
/// This is a static class to ensure a single, globally accessible instance.
/// </summary>
public class WatchdogManager : IWatchdogManager, IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    private readonly SemaphoreSlim _mutex = new(1, 1);

    private NamedPipeServerStream? _serverStream;
    private Process? _watchdogProcess;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WatchdogManager> _logger;

    /// <summary>
    /// Manages the lifecycle and communication with the Watchdog process.
    /// This is a static class to ensure a single, globally accessible instance.
    /// </summary>
    public WatchdogManager(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WatchdogManager>();
    }

    public async Task InitializeAsync()
    {
        if (_serverStream != null)
        {
            throw new InvalidOperationException("WatchdogManager is already initialized.");
        }

        var pipeName = $"Everywhere.Watchdog-{Guid.CreateVersion7().ToString()[..8]}";
        _serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out, // The host application only sends commands.
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        // 1. Start the Watchdog process.
        _logger.LogDebug("Launching Watchdog process with pipe name: {PipeName}", pipeName);
        _watchdogProcess = Process.Start(
            new ProcessStartInfo
            {
#if MACOS
                FileName = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../Helpers/Everywhere.Watchdog")),
#elif WINDOWS
                FileName = "Everywhere.Watchdog.exe",
#else
                FileName = "Everywhere.Watchdog",
#endif
                Arguments = pipeName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            });
        if (_watchdogProcess is null)
        {
            _logger.LogError("Watchdog process could not be started.");
            throw new InvalidOperationException("Failed to start Watchdog process.");
        }
        LogOutput();

        // 2. Asynchronously wait for the Watchdog client to connect.
        await _serverStream.WaitForConnectionAsync();
        _logger.LogDebug("Watchdog process connected.");

        void LogOutput()
        {
            var watchdogLogger = _loggerFactory.CreateLogger("Watchdog");
            _watchdogProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    watchdogLogger.LogDebug("{Message}", e.Data);
                    Debug.WriteLine($"[Watchdog] {e.Data}");
                }
            };
            _watchdogProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    watchdogLogger.LogError("{Message}", e.Data);
                    Debug.WriteLine($"[Watchdog] [Error] {e.Data}");
                }
            };
            _watchdogProcess.BeginOutputReadLine();
            _watchdogProcess.BeginErrorReadLine();
        }
    }

    /// <summary>
    /// Registers a subprocess to be monitored by the Watchdog.
    /// </summary>
    /// <param name="processId">The id of process to monitor.</param>
    public Task RegisterProcessAsync(int processId)
    {
        var command = new RegisterSubprocessCommand
        {
            ProcessId = processId
        };
        return SendCommandAsync(command);
    }

    /// <summary>
    /// Unregisters a subprocess from the Watchdog.
    /// </summary>
    /// <param name="processId">The id of process to stop monitoring.</param>
    /// <param name="killIfRunning"></param>
    public Task UnregisterProcessAsync(int processId, bool killIfRunning = true)
    {
        var command = new UnregisterSubprocessCommand
        {
            ProcessId = processId,
            KillIfRunning = killIfRunning
        };
        return SendCommandAsync(command);
    }

    /// <summary>
    /// Ensures the connection is established and sends a command to the Watchdog.
    /// This method is thread-safe.
    /// </summary>
    /// <param name="command">The command to send.</param>
    private async Task SendCommandAsync(WatchdogCommand command)
    {
        using (await _mutex.LockAsync())
        {
            try
            {
                if (_serverStream is not { IsConnected: true })
                {
                    throw new IOException("Watchdog is not connected.");
                }

                // Serialize and send the command with a length prefix.
                var messageBytes = MessagePackSerializer.Serialize(command);
                var lengthBytes = BitConverter.GetBytes(messageBytes.Length);

                await _serverStream.WriteAsync(lengthBytes);
                await _serverStream.WriteAsync(messageBytes);
                await _serverStream.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send command to Watchdog. Restarting Watchdog...");

                try
                {
                    await RestartWatchdogAsync();
                }
                catch (Exception restartEx)
                {
                    _logger.LogCritical(restartEx, "Failed to restart Watchdog. Running without Watchdog.");
                }
            }
        }

        async ValueTask RestartWatchdogAsync()
        {
            if (_watchdogProcess is { HasExited: false })
            {
                _watchdogProcess.Kill();
                _watchdogProcess.Dispose();
            }

            if (_serverStream is not null)
            {
                await _serverStream.DisposeAsync();
                _serverStream = null;
            }

            await InitializeAsync();
        }
    }
}

public static class WatchdogManagerExtension
{
    public static IServiceCollection AddWatchdogManager(this IServiceCollection services)
    {
        services.AddSingleton<WatchdogManager>();
        services.AddSingleton<IWatchdogManager>(x => x.GetRequiredService<WatchdogManager>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<WatchdogManager>());
        return services;
    }
}