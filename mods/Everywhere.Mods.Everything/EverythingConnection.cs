using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Avalonia.Threading;
using EverythingNet;
using Everywhere.Modding.Abstractions;
using Everywhere.ProcessIsolation.Watchdog;
using Microsoft.Win32;

namespace Everywhere.Mods.Everything;

/// <summary>
/// Serializes searches and owns only a private fallback process. User installations keep their normal lifecycle.
/// Cancellation never releases a native connection while a query is still executing.
/// </summary>
public sealed partial class EverythingConnection : IAsyncDisposable
{
    private readonly string _installationDirectory;
    private readonly string _dataDirectory;
    private readonly EverythingSettings _settings;
    private readonly IWatchdogManager _watchdog;
    private readonly CancellationTokenSource _stopping;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly string _privateInstance = "Everywhere-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
    private readonly string _privateExecutable;

    private Process? _ownedProcess;
    private WatchdogRegistration? _watchdogRegistration;
    private bool _isDisposed;

    /// <summary>
    /// Captures package locations and the activation's stopping signal without loading a native library.
    /// </summary>
    public EverythingConnection(IModContext context, IWatchdogManager watchdog, EverythingSettings settings)
    {
        _installationDirectory = context.InstallationDirectory;
        _dataDirectory = context.DataDirectory;
        _privateExecutable = Path.Combine(_dataDirectory, "runtime", "Everything.exe");
        _settings = settings;
        _watchdog = watchdog;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(context.Stopping);
    }

    /// <summary>
    /// Stops admitting queries and requests cancellation of an active SDK 3 query.
    /// SDK 2 queries remain owned until their synchronous native call returns.
    /// </summary>
    public void Stop()
    {
        if (!_isDisposed) _stopping.Cancel();
    }

    /// <summary>
    /// Runs blocking native work away from the UI thread. The caller's task owns the complete native operation,
    /// including cancellation, connection disposal and result copying; no detached timed-out query is created.
    /// </summary>
    public Task<EverythingSearchResponse> SearchAsync(string search, int maxResults, CancellationToken cancellationToken)
    {
        return Task.Run(
            async () =>
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
                lifetime.CancelAfter(TimeSpan.FromSeconds(30));
                // Read both UI-owned values together before queuing native work. Retries use this same pair.
                var options = await Dispatcher.UIThread.InvokeAsync(
                    () => new ConnectionOptions(_settings.ExecutablePath, _settings.InstanceName),
                    DispatcherPriority.Normal,
                    lifetime.Token);
                await _operation.WaitAsync(lifetime.Token).ConfigureAwait(false);

                try
                {
                    ObjectDisposedException.ThrowIf(_isDisposed, this);
                    using var client = await ConnectAsync(options, lifetime.Token).ConfigureAwait(false);
                    while (!client.IsDatabaseLoaded(lifetime.Token))
                    {
                        await Task.Delay(200, lifetime.Token).ConfigureAwait(false);
                    }

                    return client.Search(search, maxResults, lifetime.Token);
                }
                finally
                {
                    _operation.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Prefers running user instances, then a user installation, before considering the bundled executable.
    /// A configured target is authoritative: a connection failure must not silently select a different index.
    /// </summary>
    private async Task<EverythingClient> ConnectAsync(ConnectionOptions options, CancellationToken cancellationToken)
    {
        if (TryConnectUser(options) is { } running) return running;
        if (options.InstanceName is not { Length: > 0 } && options.ExecutablePath is not { Length: > 0 } &&
            _ownedProcess is { HasExited: false } && EverythingClient.TryConnect(_privateInstance) is { } owned) return owned;

        var installed = FindInstalledExecutable(options);
        if (installed is not null)
        {
            var arguments = new List<string> { "-startup" };
            if (options.InstanceName is { Length: > 0 } instance) arguments.AddRange(["-instance", instance]);
            using var launched = StartProcess(installed, arguments);
            return await WaitForConnectionAsync(() => TryConnectUser(options), cancellationToken).ConfigureAwait(false);
        }

        if (options.InstanceName is { Length: > 0 })
        {
            throw new InvalidOperationException("The configured Everything instance is unavailable. Specify ExecutablePath to start it.");
        }

        if (_ownedProcess is not { HasExited: false }) await StartPrivateAsync().ConfigureAwait(false);
        return await WaitForConnectionAsync(() => EverythingClient.TryConnect(_privateInstance), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes conventional endpoints without installing services or changing the user's settings.
    /// </summary>
    private static EverythingClient? TryConnectUser(ConnectionOptions options)
    {
        return options.InstanceName is { Length: > 0 } instance ?
            EverythingClient.TryConnect(instance) :
            EverythingClient.TryConnect() ?? EverythingClient.TryConnect("1.5a");
    }

    /// <summary>
    /// Waits for IPC to appear within the query's overall cancellation budget.
    /// </summary>
    private static async Task<EverythingClient> WaitForConnectionAsync(Func<EverythingClient?> connect, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connect() is { } client) return client;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts only the package's private instance. The existing service is reused; privileges and services
    /// are never installed or elevated implicitly. All mutable files belong to the mod data directory.
    /// </summary>
    private async Task StartPrivateAsync()
    {
        var service = FindCompatibleService();
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            if (service is null && !isAdministrator)
            {
                throw new InvalidOperationException(
                    "Everything is not running or installed, and a compatible Everything 1.5 indexing service is unavailable. Install and start Everything, " +
                    "or configure ExecutablePath for your portable installation. The mod will not install a service or request elevation.");
            }
        }

        var runtimeDirectory = Path.Combine(_dataDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        // Everything has machine-level settings beside its executable and may create ancillary files there.
        // A working copy keeps all such writes outside the immutable mod installation.
        File.Copy(GetBundledExecutable(), _privateExecutable, overwrite: true);

        var initialConfig =
            """
            [Everything]
            app_data=0
            run_as_admin=0
            index_as_admin=0
            show_tray_icon=0
            check_for_updates_on_startup=0
            """;
        if (service is not null) initialConfig += "\r\nservice_name=" + service.Name + "\r\n";
        await File.WriteAllTextAsync(Path.Combine(runtimeDirectory, "Everything.ini"), initialConfig).ConfigureAwait(false);

        var configPath = Path.Combine(_dataDirectory, "Everything.ini");
        if (!File.Exists(configPath)) await File.WriteAllTextAsync(configPath, initialConfig).ConfigureAwait(false);

        var arguments = new List<string>
        {
            "-instance", _privateInstance, "-startup", "-config", configPath,
            "-db", Path.Combine(_dataDirectory, "Everything.db")
        };
        if (service is not null) arguments.AddRange(["-service-pipe-name", service.PipeName]);

        if (_watchdogRegistration is not null) await _watchdogRegistration.DisposeAsync().ConfigureAwait(false);

        _ownedProcess?.Dispose();
        _ownedProcess = StartProcess(_privateExecutable, arguments, _dataDirectory);
        _watchdogRegistration = await _watchdog.RegisterProcessAsync(_ownedProcess).ConfigureAwait(false);
    }

    /// <summary>
    /// Locates packaged executable content independently of managed Assembly.Location and native DLL resolution.
    /// NuGet output may flatten native runtime assets; project output preserves their target paths.
    /// </summary>
    private string GetBundledExecutable()
    {
        var runtimePath = Path.Combine(_installationDirectory, "runtimes", "win-x64", "native", "Everything.exe");
        return File.Exists(runtimePath) ? runtimePath : Path.Combine(_installationDirectory, "Everything.exe");
    }

    /// <summary>
    /// Uses explicit configuration, registered App Paths and conventional installation locations.
    /// Portable installations outside these locations need an explicit ExecutablePath.
    /// </summary>
    private static string? FindInstalledExecutable(ConnectionOptions options)
    {
        if (options.ExecutablePath is { Length: > 0 } configured)
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
            {
                throw new FileNotFoundException("ExecutablePath must identify an existing absolute Everything.exe path.", configured);
            }

            return configured;
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var registry = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            foreach (var folder in new[] { "Everything", "Everything 1.5a" })
            {
                var path = Path.Combine(root, folder, "Everything.exe");
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Starts an application hidden without shell execution; arguments retain their individual boundaries.
    /// </summary>
    private static Process StartProcess(string path, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(path) ?? string.Empty
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Everything could not be started.");
    }

    /// <summary>
    /// Checks conventional installed services and their running endpoints without changing service state.
    /// Everything 1.4 and 1.5 service protocols differ; an existing pipe alone does not establish compatibility.
    /// Custom service names or pipes require using the user's configured installation instead.
    /// </summary>
    private static InstalledService? FindCompatibleService()
    {
        foreach (var service in new[]
                 {
                     new InstalledService("Everything", @"\\.\pipe\Everything Service"),
                     new InstalledService("Everything (1.5a)", @"\\.\pipe\Everything 1.5a Service")
                 })
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service.Name);
            if (key?.GetValue("ImagePath") is not string command) continue;

            command = command.TrimStart();
            var isQuoted = command.StartsWith('"');
            var start = isQuoted ? 1 : 0;
            var end = isQuoted ? command.IndexOf('"', 1) : command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < start) continue;
            if (!isQuoted) end += 4;
            var path = command[start..end];
            if (!File.Exists(path)) continue;

            var version = FileVersionInfo.GetVersionInfo(path);
            if (version.FileMajorPart != 1 || version.FileMinorPart < 5) continue;
            if (WaitNamedPipe(service.PipeName, 1) || Marshal.GetLastPInvokeError() == 121) return service;
        }

        return null;
    }

    /// <summary>
    /// Asks Windows whether an existing indexing-service pipe has an available server instance.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, uint timeout);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Stop();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed) return;
            _isDisposed = true;
            if (_ownedProcess is not { HasExited: false } process) return;

            // Only the unique instance created above receives -exit. Never call SDK 2's global Everything_Exit.
            using var exit = StartProcess(_privateExecutable, ["-instance", _privateInstance, "-exit"]);
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                process.Kill();
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (_watchdogRegistration is not null) await _watchdogRegistration.DisposeAsync().ConfigureAwait(false);
            _watchdogRegistration = null;
            _ownedProcess?.Dispose();
            _ownedProcess = null;
            _stopping.Dispose();
            _operation.Release();
        }
    }

    /// <summary>
    /// Identifies one verified 1.5 service and its conventional IPC endpoint.
    /// </summary>
    /// <param name="Name">The Windows service name used in Everything's machine configuration.</param>
    /// <param name="PipeName">The full endpoint path used by the matching service.</param>
    private sealed record InstalledService(string Name, string PipeName);

    /// <summary>
    /// Captures the UI's connection preferences for the complete lifetime of one query.
    /// </summary>
    /// <param name="ExecutablePath">The optional executable to start.</param>
    /// <param name="InstanceName">The optional exact IPC endpoint.</param>
    private sealed record ConnectionOptions(string? ExecutablePath, string? InstanceName);
}