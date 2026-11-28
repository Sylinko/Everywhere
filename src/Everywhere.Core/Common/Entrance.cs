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

    private static Action? _onExited;

#if DEBUG
    private static bool _isInitialized;
#endif

    /// <summary>
    /// Initializes Main's shared runtime and returns a scope that disposes any remaining host
    /// container, releases the asynchronous single-instance lock, flushes logs, and invokes
    /// any registered post-exit callback when disposed.
    /// </summary>
    public static IAsyncDisposable Initialize(IAsyncDisposable instanceLock)
    {
        try
        {
            InitializeCore();
            return new AsyncEntranceScope(instanceLock);
        }
        catch
        {
            instanceLock.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>
    /// Initializes Main's shared runtime and returns a scope that disposes any remaining host
    /// container, releases the single-instance lock, flushes logs, and invokes any registered
    /// post-exit callback when disposed.
    /// </summary>
    public static IDisposable Initialize(IDisposable instanceLock)
    {
        try
        {
            InitializeCore();
            return new EntranceScope(instanceLock);
        }
        catch
        {
            instanceLock.Dispose();
            throw;
        }
    }

    internal static void RegisterExitedCallback(Action? callback) => _onExited = callback;

    private static void InitializeCore()
    {
#if DEBUG
        Debug.Assert(!_isInitialized, $"{nameof(Entrance)}.{nameof(Initialize)} must be called only once.");
        _isInitialized = true;
#endif
        Debug.Assert(_onExited is null);

        InitializeRuntimeConstants();
        Telemetry.Initialize();
        InitializeLogger();
        InitializeErrorHandling();
    }

    private static async ValueTask DisposeScopeAsync(Func<ValueTask> disposeLockAsync)
    {
        try
        {
            await ServiceLocator.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await disposeLockAsync().ConfigureAwait(false);
            }
            finally
            {
                CompleteProcessExit();
            }
        }
    }

    private static void CompleteProcessExit()
    {
        var callback = _onExited;
        _onExited = null;
        try
        {
            if (callback is null)
            {
                return;
            }

            try
            {
                callback();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Unhandled exception in application exited callback.");
            }
        }
        finally
        {
            Log.CloseAndFlush();
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
                LocaleKey.Common_CriticalError.I18N(),
                string.Format(LocaleKey.Entrance_FailedToInitializeRuntimeConstants.I18N(), ex),
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
                rollingInterval: RollingInterval.Day,
                shared: true)
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

    private sealed class AsyncEntranceScope(IAsyncDisposable instanceLock) : IAsyncDisposable
    {
#if DEBUG
        private bool _isDisposed;
#endif

        public ValueTask DisposeAsync()
        {
#if DEBUG
            Debug.Assert(!_isDisposed, $"{nameof(AsyncEntranceScope)} was disposed more than once.");
            _isDisposed = true;
#endif
            return DisposeScopeAsync(instanceLock.DisposeAsync);
        }
    }

    private sealed class EntranceScope(IDisposable instanceLock) : IDisposable
    {
#if DEBUG
        private bool _isDisposed;
#endif

        public void Dispose()
        {
#if DEBUG
            Debug.Assert(!_isDisposed, $"{nameof(EntranceScope)} was disposed more than once.");
            _isDisposed = true;
#endif
            DisposeScopeAsync(() =>
                {
                    instanceLock.Dispose();
                    return ValueTask.CompletedTask;
                })
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }
}
