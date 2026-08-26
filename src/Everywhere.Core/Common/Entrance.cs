using System.Diagnostics;
using System.IO.Pipes;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Interop;
using Everywhere.Messages;
using MessagePack;
using PuppeteerSharp;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;
#if DEBUG
using Avalonia.Controls;
#endif

namespace Everywhere.Common;

public static class Entrance
{
    public static event EventHandler<UnobservedTaskExceptionEventArgs>? UnobservedTaskExceptionFilter;

    private const string BundleName = "com.sylinko.everywhere";
    private static EntranceStartup? _startup;

    public static EntranceStartup Initialize(string[] args)
    {
        var startup = InitializeSingleInstance(args);
        _startup = startup;
        if (!startup.IsPrimary)
        {
            return startup;
        }

        try
        {
            InitializeRuntimeConstants();
            Telemetry.Initialize();
            InitializeLogger();
            InitializeErrorHandling();
            return startup;
        }
        catch
        {
            startup.Abort();
            throw;
        }
    }

    /// <summary>
    /// Releases the single-instance mutex before an intentional process replacement.
    /// The activation pipe remains owned by the startup session until normal cleanup.
    /// </summary>
    public static void ReleaseMutex() => _startup?.ReleaseMutex();

    /// <summary>
    /// Initializes the application mutex to ensure a single instance of the application.
    /// </summary>
    private static EntranceStartup InitializeSingleInstance(string[] args)
    {
#if DEBUG
        if (Design.IsDesignMode) return EntranceStartup.CreatePrimary();
#endif

        var appMutex = new Mutex(true, BundleName, out var createdNew);
        if (createdNew)
        {
            var lifetime = new CancellationTokenSource();
            var pipeServerTask = StartHostPipeServer(lifetime.Token);
            return EntranceStartup.CreatePrimary(appMutex, lifetime, pipeServerTask);
        }

        appMutex.Dispose();

        if (args.Contains("--autorun"))
        {
            // Autorun, if there is already an instance, exits without contacting the primary instance.
            return EntranceStartup.CreateExit();
        }

#if IsWindows
        if (args.FirstOrDefault(x => x.StartsWith($"{UrlProtocolCallbackMessage.Scheme}:")) is { } url)
        {
            // Bring the existing instance to the foreground.
            return EntranceStartup.CreateForward(SendToHostAsync(new UrlProtocolCallbackMessage(url)));
        }
#endif

        // Bring the existing instance to the foreground.
        return EntranceStartup.CreateForward(SendToHostAsync(new ShowWindowMessage(ShowWindowMessage.ChatWindow)));
    }

    private static async Task StartHostPipeServer(CancellationToken cancellationToken)
    {
        const int maxRetries = 5;
        var consecutiveErrors = 0;

        while (consecutiveErrors < maxRetries)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    BundleName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var lengthBuffer = new byte[4];
                await server.ReadExactlyAsync(lengthBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);

                var length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length is <= 0 or > 1024 * 1024) // sanity check: max 1 MB
                {
                    Log.ForContext(typeof(Entrance)).Warning("Received invalid command length: {Length}", length);
                    continue;
                }

                var buffer = new byte[length];
                await server.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

                try
                {
                    var command = MessagePackSerializer.Deserialize<ApplicationMessage>(buffer);
                    WeakReferenceMessenger.Default.Send(command);
                }
                catch (Exception ex)
                {
                    Log.ForContext(typeof(Entrance)).Error(ex, "Failed to deserialize host command.");
                }

                // Reset error counter on successful processing
                consecutiveErrors = 0;
            }
            catch (EndOfStreamException)
            {
                // Client disconnected before sending complete data; not a server error, just retry
                Log.ForContext(typeof(Entrance)).Warning("Pipe client disconnected prematurely.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.ForContext(typeof(Entrance)).Error(ex, "Host pipe server error.");

                consecutiveErrors++;
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (server != null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            Log.ForContext(typeof(Entrance)).Error(
                "Host pipe server stopped after {MaxRetries} consecutive errors.", maxRetries);
        }
    }

    private static async Task<int> SendToHostAsync(ApplicationMessage message)
    {
        const int maxAttempts = 3;
        const int connectTimeoutMs = 5000;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(".", BundleName, PipeDirection.Out, PipeOptions.Asynchronous);
                await client.ConnectAsync(connectTimeoutMs).ConfigureAwait(false);

                var bytes = MessagePackSerializer.Serialize(message);
                var lengthBytes = BitConverter.GetBytes(bytes.Length);

                await client.WriteAsync(lengthBytes).ConfigureAwait(false);
                await client.WriteAsync(bytes).ConfigureAwait(false);
                await client.FlushAsync().ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Log.Error(ex, "Failed to send command to host instance (attempt {Attempt}/{MaxAttempts}).", attempt, maxAttempts);
                await Task.Delay(500 * attempt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to send command to host instance after {MaxAttempts} attempts.", maxAttempts);

                // Show message box if the command is ShowMainWindowCommand as a fallback.
                if (message is ShowWindowMessage)
                {
                    NativeMessageBox.Show(
                        LocaleResolver.Common_Info,
                        LocaleResolver.Entrance_EverywhereAlreadyRunning,
                        NativeMessageBoxButtons.Ok,
                        NativeMessageBoxIcon.Information);
                }
            }
        }

        return 0;
    }

    private static void InitializeRuntimeConstants()
    {
        try
        {
            // Accessing DeviceId to trigger its initialization and catch any potential exceptions early
            _ = RuntimeConstants.DeviceId;
        }
        catch (Exception ex)
        {
            NativeMessageBox.Show(
                LocaleResolver.Common_CriticalError,
                string.Format(LocaleResolver.Entrance_FailedToInitializeRuntimeConstants, ex),
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Error);
            throw new InvalidOperationException("Failed to initialize runtime constants.", ex);
        }
    }

    private static void InitializeLogger()
    {
        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#endif
            .Enrich.FromLogContext()
            .Enrich.With<ActivityEnricher>()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                new JsonFormatter(),
                Path.Combine(RuntimeConstants.EnsureWritableDataFolderPath("logs"), ".jsonl"),
                rollingInterval: RollingInterval.Day)
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(logEvent =>
                    logEvent.Properties.TryGetValue("SourceContext", out var sourceContextValue) &&
                    sourceContextValue.As<ScalarValue>()?.Value?.ToString()?.StartsWith("Everywhere.") is true)
                .Filter.ByExcluding(logEvent => logEvent.Exception.Segregate()
                    .AsValueEnumerable()
                    .Any(e => e is
                        OperationCanceledException or
                        TimeoutException or
                        HandledException { IsExpected: true } or
                        PuppeteerException))
                .WriteTo.Sentry(LogEventLevel.Error, LogEventLevel.Information))
            .CreateLogger();
    }

    private static void InitializeErrorHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            Log.Logger.Error(e.ExceptionObject as Exception, "Unhandled Exception");
        };

        TaskScheduler.UnobservedTaskException += static (s, e) =>
        {
            UnobservedTaskExceptionFilter?.Invoke(s, e);
            if (e.Observed) return;

            Log.Logger.Error(e.Exception, "Unobserved Task Exception");
            e.SetObserved();
        };
    }

    private sealed class ActivityEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (Activity.Current is not { } activity) return;

            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(
                    nameof(activity.TraceId),
                    activity.TraceId)
            );
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(
                    nameof(activity.SpanId),
                    activity.SpanId)
            );
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(
                    "ActivityId",
                    activity.Id)
            );
        }
    }
}

/// <summary>
/// Owns the single-instance resources created during process bootstrap. A primary
/// instance owns the mutex and activation pipe; a secondary instance owns only the
/// asynchronous operation that forwards its activation request.
/// </summary>
public sealed class EntranceStartup : IAsyncDisposable
{
    public bool IsPrimary { get; }

    private readonly Mutex? _appMutex;
    private readonly CancellationTokenSource? _lifetime;
    private readonly Task? _pipeServerTask;
    private readonly Task<int>? _forwardTask;

    private int _isDisposed;
    private int _isMutexReleased;

    private EntranceStartup(
        bool isPrimary,
        Mutex? appMutex = null,
        CancellationTokenSource? lifetime = null,
        Task? pipeServerTask = null,
        Task<int>? forwardTask = null)
    {
        IsPrimary = isPrimary;
        _appMutex = appMutex;
        _lifetime = lifetime;
        _pipeServerTask = pipeServerTask;
        _forwardTask = forwardTask;
    }

    public Task<int> ForwardAsync() =>
        _forwardTask ?? throw new InvalidOperationException("The primary instance has no forwarding operation.");

    internal static EntranceStartup CreatePrimary() => new(true);

    internal static EntranceStartup CreatePrimary(Mutex appMutex, CancellationTokenSource lifetime, Task pipeServerTask) =>
        new(true, appMutex, lifetime, pipeServerTask);

    internal static EntranceStartup CreateExit() => new(false, forwardTask: Task.FromResult(0));

    internal static EntranceStartup CreateForward(Task<int> forwardTask) => new(false, forwardTask: forwardTask);

    /// <summary>
    /// Releases resources without waiting. This is used only when the synchronous
    /// bootstrap sequence fails before the outer async lifetime can take ownership.
    /// </summary>
    internal void Abort()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _lifetime?.Cancel();
        ReleaseMutex();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                if (_pipeServerTask is { } pipeServerTask)
                {
                    await pipeServerTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                _lifetime.Dispose();
            }
        }

        ReleaseMutex();
    }

    internal void ReleaseMutex()
    {
        if (Interlocked.Exchange(ref _isMutexReleased, 1) != 0 || _appMutex is null)
        {
            return;
        }

        try
        {
            _appMutex.ReleaseMutex();
        }
        finally
        {
            _appMutex.Dispose();
        }
    }
}