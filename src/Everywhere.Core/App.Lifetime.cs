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
    /// Requests application cancellation and closes the desktop loop. Final cleanup belongs
    /// to the platform entry point, not the requesting component or the UI dispatcher.
    /// </summary>
    public void Shutdown(int exitCode = 0)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isShutdownRequested)
            {
                return;
            }

            _isShutdownRequested = true;
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
    /// Called once by the platform entry point after the desktop loop ends. Cancellation also
    /// covers a loop that throws; the observed application task completes before DI disposal.
    /// </summary>
    public Task WaitForShutdownAsync()
    {
        _lifetime.Cancel();
        return _applicationTask;
    }

    /// <summary>
    /// Releases the application cancellation source. The platform calls this after waiting for
    /// the application task and disposing host services; it does not initiate another shutdown.
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
            cancellationToken).GetTask();
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
                    cancellationToken).GetTask().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
