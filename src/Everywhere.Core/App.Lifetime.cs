using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.ProcessIsolation.Activation;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Everywhere;

public partial class App : IDisposable
{
    /// <summary>
    /// Shared by startup and application-lived work; released after the host container.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// Tracks initialization + ready publication so shutdown during startup drains before stopping the UI loop.
    /// </summary>
    private Task _startupTask = Task.CompletedTask;

    /// <summary>
    /// The platform entry point waits for this task after the desktop loop ends.
    /// </summary>
    private Task _applicationTask = Task.CompletedTask;

    /// <summary>
    /// UI-thread-owned flag preventing repeated desktop exit requests and late UI publication.
    /// </summary>
    private bool _isShutdownRequested;

    /// <summary>
    /// Creates an <see cref="AppBuilder"/> configured with <see cref="App"/>, fonts, and tracing.
    /// </summary>
    public static AppBuilder CreateBuilder(IServiceProvider serviceProvider) =>
        AppBuilder.Configure(() => new App(serviceProvider)).WithInterFont().LogToTrace();

    internal static async Task<int> RunClassicDesktopCoreAsync(AppBuilder builder, string[] args)
    {
        try
        {
            try
            {
                return builder.StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
            }
            finally
            {
                await CompleteDesktopShutdownAsync(Current as App).ConfigureAwait(false);
            }
        }
        catch
        {
            Entrance.RegisterExitedCallback(null);
            throw;
        }
    }

    private static async Task CompleteDesktopShutdownAsync(App? app)
    {
        // Keep the shared token source alive until every host service has been disposed.
        using (app)
        {
            try
            {
                // Also drain startup when the desktop loop throws instead of returning normally.
                if (app is not null)
                {
                    await app.WaitForShutdownAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await ServiceLocator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Requests application shutdown and relaunches a fresh process with the supplied
    /// command-line arguments once all single-instance locks and host resources are released.
    /// </summary>
    public void Restart(params IReadOnlyList<string> arguments)
    {
        Shutdown(onExited: TryRelaunchCurrentProcess);

        // Launches a fresh instance of the current executable or macOS app bundle. Callers invoke this
        // only after single-instance locks and host resources are completely released.
        void TryRelaunchCurrentProcess()
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                Log.Logger.Error("Cannot restart Everywhere because Environment.ProcessPath is unavailable.");
                return;
            }

            try
            {
                if (OperatingSystem.IsMacOS() && TryGetMacAppBundlePath(processPath) is { } bundlePath)
                {
                    var macStartInfo = new ProcessStartInfo("open") { UseShellExecute = false };
                    macStartInfo.ArgumentList.Add("-n");
                    macStartInfo.ArgumentList.Add(bundlePath);
                    if (arguments is { Count: > 0 })
                    {
                        macStartInfo.ArgumentList.Add("--args");
                        foreach (var argument in arguments)
                        {
                            macStartInfo.ArgumentList.Add(argument);
                        }
                    }

                    using var macProcess = Process.Start(macStartInfo);
                    return;
                }

                var startInfo = new ProcessStartInfo(processPath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory
                };
                if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase) &&
                    Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entryAssembly)
                {
                    startInfo.ArgumentList.Add(entryAssembly);
                }

                if (arguments is { Count: > 0 })
                {
                    foreach (var argument in arguments)
                    {
                        startInfo.ArgumentList.Add(argument);
                    }
                }

                using var process = Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                Log.Logger.Error(exception, "Failed to relaunch Everywhere after restart request.");
            }
        }

        static string? TryGetMacAppBundlePath(string executablePath)
        {
            var macOsDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (!string.Equals(Path.GetFileName(macOsDirectory), "MacOS", StringComparison.Ordinal))
            {
                return null;
            }

            var contentsDirectory = Path.GetDirectoryName(macOsDirectory);
            if (!string.Equals(Path.GetFileName(contentsDirectory), "Contents", StringComparison.Ordinal))
            {
                return null;
            }

            var bundleDirectory = Path.GetDirectoryName(contentsDirectory);
            return bundleDirectory is { Length: > 0 } && bundleDirectory.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? bundleDirectory
                : null;
        }
    }

    /// <summary>
    /// Requests application cancellation and closes the desktop loop. Host cleanup and optional
    /// post-exit callback execution belong to <see cref="AppBuilderExtensions.RunClassicDesktopAsync"/>
    /// and <see cref="Entrance"/>.
    /// </summary>
    public void Shutdown(int exitCode = 0, Action? onExited = null)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isShutdownRequested)
            {
                return;
            }

            _isShutdownRequested = true;
            Entrance.RegisterExitedCallback(onExited);
            _lifetime.Cancel();
            TrayIcon.SetIcons(this, null);

            if (ApplicationLifetime is not IControlledApplicationLifetime lifetime)
            {
                return;
            }

            if (_startupTask.IsCompleted)
            {
                lifetime.Shutdown(exitCode);
            }
            else
            {
                // Startup can own a dialog whose cancellation and continuations require the dispatcher.
                ShutdownAfterStartupAsync(lifetime, exitCode).Detach(NativeMessageBox.ExceptionHandler);
            }
        });
    }

    private async Task ShutdownAfterStartupAsync(IControlledApplicationLifetime lifetime, int exitCode)
    {
        try
        {
            await _startupTask;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Logger.Debug(ex, "Application startup failed while draining shutdown.");
        }

        lifetime.Shutdown(exitCode);
    }

    /// <summary>
    /// Cancels the application lifetime and waits for background application tasks to drain before DI disposal.
    /// </summary>
    public Task WaitForShutdownAsync()
    {
        _lifetime.Cancel();
        return _applicationTask;
    }

    /// <summary>
    /// Releases the application cancellation source after waiting for application tasks and disposing host services.
    /// </summary>
    public void Dispose() => _lifetime.Dispose();

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        if (Design.IsDesignMode || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        desktop.ShutdownRequested += (_, args) =>
        {
            if (!_startupTask.IsCompleted)
            {
                // Unfinished startup needs the dispatcher to drain before exit.
                args.Cancel = true;
                Shutdown();
                return;
            }

            // External shutdown requests only signal cancellation; the platform owns final cleanup.
            _isShutdownRequested = true;
            _lifetime.Cancel();
        };
        desktop.Exit += (_, _) =>
        {
            _isShutdownRequested = true;
            _lifetime.Cancel();
        };

        var startupTask = Task.Run(RunStartupAsync);
        _startupTask = startupTask;
        _applicationTask = ObserveApplicationAsync(startupTask);
    }

    /// <summary>
    /// Observes the single run task and reports startup failure without waiting for UI shutdown.
    /// </summary>
    private async Task ObserveApplicationAsync(Task startupTask)
    {
        try
        {
            await Task.Run(() => RunApplicationAsync(startupTask)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.PostOnDemand(() =>
            {
                if (_isShutdownRequested)
                {
                    return;
                }

                // A failed initializer also cancels the shared token. Only an actual exit request
                // makes cancellation expected; an independent timeout still reports startup failure.
                Log.Logger.Fatal(exception, "Application startup or activation delivery failed.");
                NativeMessageBox.Show(
                    "Application Error",
                    $"An error occurred during application startup or activation:\n{exception.Message}\n\nPlease check the logs for more details.",
                    NativeMessageBoxButtons.Ok,
                    NativeMessageBoxIcon.Error);
                Shutdown(1);
            });
        }
    }

    /// <summary>
    /// Publishes application UI only after initialization, while the dispatcher is still available.
    /// </summary>
    private Task PublishReadyAsync(CancellationToken cancellationToken)
    {
        return Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RecordAppLaunchMetric();
                    TrayIcon.SetIcons(this, [new MainTrayIcon(this, serviceProvider)]);
                    ShowMainWindowOnNeeded();
                },
                DispatcherPriority.Normal,
                cancellationToken)
            .GetTask();
    }

    /// <summary>
    /// Initializes services and publishes readiness before activation delivery begins.
    /// </summary>
    private async Task RunStartupAsync()
    {
        var cancellationToken = _lifetime.Token;
        try
        {
            await InitializeServicesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await PublishReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _lifetime.CancelAsync();
            throw;
        }
    }

    /// <summary>
    /// Waits for startup and delivers activations until exit.
    /// The platform retains the activation endpoint claim through final container disposal.
    /// </summary>
    private async Task RunApplicationAsync(Task startupTask)
    {
        var cancellationToken = _lifetime.Token;
        var activationServer = serviceProvider.GetService<ApplicationActivationServer>();
        try
        {
            await startupTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (activationServer is not null)
            {
                await DispatchActivationsAsync(activationServer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await _lifetime.CancelAsync();
            throw;
        }
        finally
        {
            if (activationServer is not null)
            {
                await activationServer.StopAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Resolves one ordered initializer group at a time. A failed step cancels the shared
    /// application token, and the started group completes before later services can be released.
    /// </summary>
    private async Task InitializeServicesAsync()
    {
        var cancellationToken = _lifetime.Token;
        foreach (var group in serviceProvider.GetServices<AsyncInitializerRegistration>().GroupBy(i => i.Index).OrderBy(g => g.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAll(group.Select(InitializeAsync)).ConfigureAwait(false);
        }

        async Task InitializeAsync(AsyncInitializerRegistration registration)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await registration.InitializeAsync(serviceProvider, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _lifetime.CancelAsync();
                throw;
            }
        }
    }

    /// <summary>
    /// Delivers queued activations through cancellable UI operations until application exit.
    /// </summary>
    private static async Task DispatchActivationsAsync(ApplicationActivationServer server, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in server.ReadPendingAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplicationMessage message = request switch
                {
                    UrlCallbackActivationRequest callback => new UrlProtocolCallbackMessage(callback.Url),
                    ShowChatWindowActivationRequest => new ShowWindowMessage(ShowWindowMessage.ChatWindow),
                    _ => throw new NotSupportedException($"Unsupported activation request type: {request.GetType().FullName}")
                };

                await Dispatcher.UIThread.InvokeAsync(
                        () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            WeakReferenceMessenger.Default.Send(message);
                        },
                        DispatcherPriority.Normal,
                        cancellationToken)
                    .GetTask()
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// Provides desktop lifetime runners for <see cref="AppBuilder"/> instances created by <see cref="App.CreateBuilder"/>.
/// </summary>
public static class AppBuilderExtensions
{
    extension(AppBuilder builder)
    {
        /// <summary>
        /// Starts the classic desktop lifetime, drains shutdown and host services, and forwards any
        /// registered post-exit callback to <see cref="Entrance"/>.
        /// </summary>
        public Task<int> RunClassicDesktopAsync(string[] args) =>
            App.RunClassicDesktopCoreAsync(builder, args);

        /// <summary>
        /// Synchronously starts the classic desktop lifetime on the calling main thread, drains
        /// shutdown and host services, and forwards any registered post-exit callback to <see cref="Entrance"/>.
        /// </summary>
        public int RunClassicDesktop(string[] args) =>
            App.RunClassicDesktopCoreAsync(builder, args).GetAwaiter().GetResult();
    }
}
