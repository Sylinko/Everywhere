using System.Diagnostics;
using Everywhere.Interop;
using PuppeteerSharp;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace Everywhere.Common;

public static class Entrance
{
    /// <summary>
    /// Raised when a task exception is unobserved. The default handler logs the exception and marks it as observed.
    /// </summary>
    public static event EventHandler<UnobservedTaskExceptionEventArgs>? UnobservedTaskExceptionFilter;

    /// <summary>
    /// Initializes Main's shared runtime after the platform entry point has claimed its single-instance lifetime.
    /// Host, controller, and secondary processes must not initialize this runtime.
    /// </summary>
    public static void Initialize()
    {
        InitializeRuntimeConstants();
        Telemetry.Initialize();
        InitializeLogger();
        InitializeErrorHandling();
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