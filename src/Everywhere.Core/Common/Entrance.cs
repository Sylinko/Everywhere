using System.Diagnostics;
using System.IO.Pipes;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using MessagePack;
using PuppeteerSharp;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;
using ZLinq;
#if DEBUG
using Avalonia.Controls;
#endif

namespace Everywhere.Common;

public static class Entrance
{
    public static event EventHandler<UnobservedTaskExceptionEventArgs>? UnobservedTaskExceptionFilter;

    private static Mutex? _appMutex;

    private const string BundleName = "com.sylinko.everywhere";

    /// <summary>
    /// Releases the application mutex. Only call this method when the application is exiting.
    /// </summary>
    public static void ReleaseMutex()
    {
        if (_appMutex == null) return;

        _appMutex.ReleaseMutex();
        _appMutex.Dispose();
        _appMutex = null;
    }

    public static async ValueTask InitializeAsync(string[] args)
    {
        await InitializeSingleInstanceAsync(args);
        InitializeRuntimeConstants();
        Telemetry.Initialize();
        InitializeLogger();
        InitializeErrorHandling();
    }

    /// <summary>
    /// Initializes the application mutex to ensure a single instance of the application.
    /// </summary>
    private static async ValueTask InitializeSingleInstanceAsync(string[] args)
    {
#if DEBUG
        if (Design.IsDesignMode) return;
#endif

        _appMutex = new Mutex(true, BundleName, out var createdNew);
        if (createdNew)
        {
            Task.Run(StartHostPipeServer).Detach(Log.ForContext(typeof(Entrance)).ToExceptionHandler());
            return;
        }

        if (args.Contains("--autorun"))
        {
            // Autorun, if there is already an instance, exit immediately
            Environment.Exit(0);
            return;
        }

#if IsWindows
        if (args.FirstOrDefault(x => x.StartsWith($"{UrlProtocolCallbackMessage.Scheme}:")) is { } url)
        {
            // Bring the existing instance to the foreground.
            await SendToHost(new UrlProtocolCallbackMessage(url)).ConfigureAwait(false);
            Environment.Exit(0);
            return;
        }
#endif

        // Bring the existing instance to the foreground.
        await SendToHost(new ShowWindowMessage(ShowWindowMessage.ChatWindow)).ConfigureAwait(false);
        Environment.Exit(0);
    }

    private static async Task StartHostPipeServer()
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

                await server.WaitForConnectionAsync();

                var lengthBuffer = new byte[4];
                await server.ReadExactlyAsync(lengthBuffer.AsMemory(0, 4));

                var length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length is <= 0 or > 1024 * 1024) // sanity check: max 1 MB
                {
                    Log.ForContext(typeof(Entrance)).Warning("Received invalid command length: {Length}", length);
                    continue;
                }

                var buffer = new byte[length];
                await server.ReadExactlyAsync(buffer.AsMemory(0, length));

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
            catch (Exception ex)
            {
                Log.ForContext(typeof(Entrance)).Error(ex, "Host pipe server error.");

                consecutiveErrors++;
                await Task.Delay(1000);
            }
            finally
            {
                if (server != null)
                {
                    await server.DisposeAsync();
                }
            }
        }

        Log.ForContext(typeof(Entrance)).Error(
            "Host pipe server stopped after {MaxRetries} consecutive errors.", maxRetries);
    }

    private static async Task SendToHost(ApplicationMessage message)
    {
        const int maxAttempts = 3;
        const int connectTimeoutMs = 5000;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(".", BundleName, PipeDirection.Out, PipeOptions.Asynchronous);
                await client.ConnectAsync(connectTimeoutMs);

                var bytes = MessagePackSerializer.Serialize(message);
                var lengthBytes = BitConverter.GetBytes(bytes.Length);

                await client.WriteAsync(lengthBytes);
                await client.WriteAsync(bytes);
                await client.FlushAsync();
                return; // success
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Log.Error(ex, "Failed to send command to host instance (attempt {Attempt}/{MaxAttempts}).", attempt, maxAttempts);
                await Task.Delay(500 * attempt);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to send command to host instance after {MaxAttempts} attempts.", maxAttempts);

                // Show message box if the command is ShowMainWindowCommand as a fallback
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
            Environment.Exit(1);
        }
    }

    private static void InitializeLogger()
    {
        Log.Logger = new LoggerConfiguration()
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