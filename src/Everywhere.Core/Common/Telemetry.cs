using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PuppeteerSharp;

namespace Everywhere.Common;

public static partial class Telemetry
{
    /// <summary>
    /// Suppresses traces, new breadcrumbs, and cloud logs below Warning without disabling error reporting or metrics.
    /// The current value is checked when spans end and new SDK logs or breadcrumbs are captured.
    /// </summary>
    public static bool SendOnlyNecessaryData { get; set; }

    private static SentryMeterListener? _sentryMeterListener;

#if DEBUG
    private const string EnvironmentName = "debug";
    private const double TraceSampleRate = 1.0;
#else
    private const string EnvironmentName = "stable";
    private const double TraceSampleRate = 0.2;
#endif

    /// <summary>
    /// Initializes SDK event reporting and the separate OTLP tracing pipeline for the main application process.
    /// </summary>
    public static void Initialize()
    {
        if (string.IsNullOrEmpty(SentryDsn)) return;

        var sentry = SentrySdk.Init(ConfigureSentry);

        SentrySdk.ConfigureScope(scope =>
        {
            scope.User.Id = RuntimeConstants.DeviceId;
            scope.User.Username = null;
            scope.User.IpAddress = null;
            scope.User.Email = null;
        });

        var traceProvider = CreateTraceProviderBuilder().AddSentryOtlpExporter(SentryDsn).Build();

        // Start listening to System.Diagnostics.Metrics and forwarding to Sentry.
        // Metrics remain enabled in both diagnostic modes, with the existing device ID tag.
        _sentryMeterListener = new SentryMeterListener();

        AppDomain.CurrentDomain.ProcessExit += delegate
        {
            _sentryMeterListener.Dispose();
            traceProvider.Dispose();
            sentry.Dispose();
        };
    }

    private static void ConfigureSentry(SentryOptions options)
    {
        options.Dsn = SentryDsn;
        options.AutoSessionTracking = true;
        options.IsGlobalModeEnabled = true;
        options.EnableLogs = true;
        options.Environment = EnvironmentName;
        options.Release = RuntimeConstants.Version.ToString();
#if DEBUG
        options.Debug = true;
#endif
        options.UseOtlp();
        options.SetBeforeSend(evt =>
            evt.Exception.Segregate()
                .AsValueEnumerable()
                .Any(e => e is
                    OperationCanceledException or
                    TimeoutException or
                    HandledException { IsExpected: true } or
                    PuppeteerException) ? // No one knows why PuppeteerSharp throws so many exceptions and leave them unhandled in Task.Run
                null :
                evt);
        options.SetBeforeBreadcrumb(breadcrumb => SendOnlyNecessaryData ? null : breadcrumb);
        options.SetBeforeSendLog(log => SendOnlyNecessaryData && log.Level < SentryLogLevel.Warning ? null : log);
    }

    private static TracerProviderBuilder CreateTraceProviderBuilder()
    {
        return Sdk
            .CreateTracerProviderBuilder()
            .AddSource("Everywhere.*")
            .SetResourceBuilder(
                ResourceBuilder.CreateDefault()
                    .AddService("Everywhere", serviceVersion: RuntimeConstants.Version.ToString())
                    .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", EnvironmentName)]))
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(TraceSampleRate)))
            // This processor must run before the exporter's batch processor accepts an ended span.
            .AddProcessor(new SentryTraceProcessor());
    }

    private sealed class SentryTraceProcessor : BaseProcessor<Activity>
    {
        /// <inheritdoc />
        public override void OnStart(Activity activity)
        {
            // SDK scope fields are not automatically copied into the separate OTLP payload.
            activity.SetTag("user.id", RuntimeConstants.DeviceId);
        }

        /// <inheritdoc />
        public override void OnEnd(Activity activity)
        {
            if (SendOnlyNecessaryData)
            {
                // Recheck at completion: the user may have opted out while this span was running.
                activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            }
        }
    }

    /// <summary>
    /// Bridges <see cref="System.Diagnostics.Metrics"/> instruments to Sentry Metrics API.
    /// Listens on all meters whose name starts with "Everywhere." and forwards measurements
    /// to SentrySdk.Metrics.
    /// </summary>
    /// <remarks>
    /// Sentry currently has no native OpenTelemetry Metrics export, so we use
    /// <see cref="MeterListener"/> to manually bridge the gap. When Sentry adds native support,
    /// this class can be replaced with a standard OTel MeterProvider exporter.
    /// </remarks>
    private sealed class SentryMeterListener : IDisposable
    {
        private readonly MeterListener _listener;

        public SentryMeterListener()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = OnInstrumentPublished,
            };

            _listener.SetMeasurementEventCallback<byte>(OnMeasurement);
            _listener.SetMeasurementEventCallback<short>(OnMeasurement);
            _listener.SetMeasurementEventCallback<int>(OnMeasurement);
            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.SetMeasurementEventCallback<float>(OnMeasurement);
            _listener.SetMeasurementEventCallback<double>(OnMeasurement);

            _listener.Start();
        }

        private static void OnInstrumentPublished(Instrument instrument, MeterListener listener)
        {
            if (instrument.Meter.Name.StartsWith("Everywhere.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        }

        private static void OnMeasurement<T>(
            Instrument instrument,
            T measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state) where T : struct
        {
            var maxPossibleLength = tags.Length + 1;
            var buffer = ArrayPool<KeyValuePair<string, object>>.Shared.Rent(maxPossibleLength);

            try
            {
                var written = 0;
                buffer[written++] = new KeyValuePair<string, object>("user.id", RuntimeConstants.DeviceId);
                foreach (var tag in tags.AsValueEnumerable())
                {
                    if (tag.Value is not null)
                    {
                        buffer[written++] = new KeyValuePair<string, object>(tag.Key, tag.Value);
                    }
                }

                switch (instrument)
                {
                    case Counter<T>:
                    {
                        SentrySdk.Metrics.EmitCounter(instrument.Name, measurement, buffer.AsSpan(0, written));
                        break;
                    }
                    case Histogram<T>:
                    {
                        SentrySdk.Metrics.EmitDistribution(instrument.Name, measurement, MapUnit(instrument.Unit), buffer.AsSpan(0, written));
                        break;
                    }
                    case Gauge<T>:
                    {
                        SentrySdk.Metrics.EmitGauge(instrument.Name, measurement, MapUnit(instrument.Unit), buffer.AsSpan(0, written));
                        break;
                    }
                }
            }
            finally
            {
                ArrayPool<KeyValuePair<string, object>>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Maps instrument unit strings to Sentry <see cref="MeasurementUnit"/>.
        /// </summary>
        private static MeasurementUnit MapUnit(string? unit) => unit switch
        {
            "ns" => MeasurementUnit.Duration.Nanosecond,
            "μs" => MeasurementUnit.Duration.Microsecond,
            "ms" => MeasurementUnit.Duration.Millisecond,
            "s" or "second" => MeasurementUnit.Duration.Second,
            "m" or "minute" => MeasurementUnit.Duration.Minute,
            "h" or "hour" => MeasurementUnit.Duration.Hour,
            "d" or "day" => MeasurementUnit.Duration.Day,
            "w" or "week" => MeasurementUnit.Duration.Week,
            "%" or "ratio" => MeasurementUnit.Fraction.Ratio,
            "/" or "percent" => MeasurementUnit.Fraction.Percent,
            null => MeasurementUnit.None,
            _ => MeasurementUnit.Custom(unit),
        };

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}