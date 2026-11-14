using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Everywhere.Common;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

namespace Everywhere.Core.Tests.Common;

[NonParallelizable]
public sealed class TelemetryTests
{
    private bool _wasNecessaryOnly;

    [SetUp]
    public void SetUp()
    {
        _wasNecessaryOnly = Telemetry.SendOnlyNecessaryData;
        Telemetry.SendOnlyNecessaryData = false;
    }

    [TearDown]
    public void TearDown()
    {
        Telemetry.SendOnlyNecessaryData = _wasNecessaryOnly;
    }

    // Privacy changes must apply to running spans without rebuilding the provider or changing error reporting.
    [Test]
    public void Tracing_WhenPrivacyChangesBeforeCompletion_FiltersSpansAndPreservesContext()
    {
        var createBuilder = typeof(Telemetry).GetMethod("CreateTraceProviderBuilder", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertionException("Missing tracing configuration.");
        var builder = createBuilder.Invoke(null, null) as TracerProviderBuilder
            ?? throw new AssertionException("Missing tracer builder.");
        var exporter = new RecordingTraceExporter();
        // Use the same recorded-span gate as the OTLP exporter's batch processor without sending data to Sentry.
        using var provider = builder.AddProcessor(new BatchActivityExportProcessor(exporter)).Build();
        using var source = new ActivitySource("Everywhere.Tests.Telemetry");
        // An explicitly sampled parent makes this regression independent of the production root sampling probability.
        var parent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        source.StartActivity("allowed-before-toggle", ActivityKind.Internal, parent)?.Dispose();

        var running = source.StartActivity("blocked-running-span", ActivityKind.Internal, parent);
        Assert.That(running, Is.Not.Null);
        Telemetry.SendOnlyNecessaryData = true;
        running.Dispose();
        source.StartActivity("blocked-new-span", ActivityKind.Internal, parent)?.Dispose();

        Telemetry.SendOnlyNecessaryData = false;
        source.StartActivity("allowed-after-toggle", ActivityKind.Internal, parent)?.Dispose();
        var unsampled = new ActivityContext(parent.TraceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.None);
        source.StartActivity("unsampled-child", ActivityKind.Internal, unsampled)?.Dispose();
        using var otherSource = new ActivitySource("Other.Tests.Telemetry");
        using var outside = otherSource.StartActivity();
        var didFlush = provider.ForceFlush(5000);

        var spans = exporter.Spans.ToArray();
        var resource = provider.GetResource().Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(didFlush, Is.True);
            Assert.That(outside, Is.Null);
            Assert.That(spans.Select(span => span.OperationName), Is.EquivalentTo<string>(["allowed-before-toggle", "allowed-after-toggle"]));
            Assert.That(spans.All(span => span.TraceId == parent.TraceId), Is.True);
            Assert.That(spans.All(span => Equals(span.GetTagItem("user.id"), RuntimeConstants.DeviceId)), Is.True);
            Assert.That(resource["service.name"], Is.EqualTo("Everywhere"));
            Assert.That(resource["service.version"], Is.EqualTo(RuntimeConstants.Version.ToString()));
#if DEBUG
            Assert.That(resource["deployment.environment.name"], Is.EqualTo("debug"));
#else
            Assert.That(resource["deployment.environment.name"], Is.EqualTo("stable"));
#endif
        }
    }

    // The tracing migration must not bypass SDK exception/breadcrumb filters or the runtime cloud-log policy.
    [Test]
    public async Task Sdk_WhenNecessaryModeChanges_KeepsErrorsAndWarningsButSuppressesInfoAndNewBreadcrumbs()
    {
        var configure = typeof(Telemetry).GetMethod("ConfigureSentry", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertionException("Missing SDK configuration.");
        var options = new SentryOptions();
        configure.Invoke(null, [options]);
        var transport = new RecordingSentryTransport();
        options.Dsn = "https://public@example.invalid/42";
        options.Transport = transport;
        options.AutoSessionTracking = false;
        options.Debug = false;
        using var sdk = SentrySdk.Init(options);
        using var activity = new Activity("error-correlation").SetIdFormat(ActivityIdFormat.W3C).Start();
        SentrySdk.AddBreadcrumb("allowed-breadcrumb");
        SentrySdk.Logger.LogInfo("allowed-info");

        Telemetry.SendOnlyNecessaryData = true;
        SentrySdk.AddBreadcrumb("blocked-breadcrumb");
        SentrySdk.Logger.LogDebug("blocked-debug-log");
        SentrySdk.Logger.LogInfo("blocked-info-log");
        SentrySdk.Logger.LogWarning("allowed-warning");
        SentrySdk.Logger.LogError("allowed-error-log");
        SentrySdk.CaptureException(new TimeoutException("filtered-timeout"));
        SentrySdk.CaptureException(new OperationCanceledException("filtered-cancellation"));
        SentrySdk.CaptureException(new InvalidOperationException("unexpected-error"));

        Telemetry.SendOnlyNecessaryData = false;
        SentrySdk.Logger.LogInfo("resumed-info");
        SentrySdk.AddBreadcrumb("resumed-breadcrumb");
        SentrySdk.CaptureMessage("resumed-event");
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        var payload = string.Join('\n', transport.Envelopes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.TracesSampleRate, Is.Null);
            Assert.That(payload, Does.Contain("unexpected-error").And.Contain(activity.TraceId.ToString()));
            Assert.That(payload, Does.Not.Contain("filtered-timeout").And.Not.Contain("filtered-cancellation"));
            Assert.That(payload, Does.Contain("allowed-breadcrumb").And.Contain("resumed-breadcrumb"));
            Assert.That(payload, Does.Not.Contain("blocked-breadcrumb"));
            Assert.That(payload, Does.Contain("allowed-info").And.Contain("resumed-info"));
            Assert.That(payload, Does.Contain("allowed-warning").And.Contain("allowed-error-log"));
            Assert.That(payload, Does.Not.Contain("blocked-info-log").And.Not.Contain("blocked-debug-log"));
        }
    }

    private sealed class RecordingTraceExporter : BaseExporter<Activity>
    {
        public ConcurrentQueue<Activity> Spans { get; } = new();

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var span in batch)
            {
                Spans.Enqueue(span);
            }

            return ExportResult.Success;
        }
    }

    private sealed class RecordingSentryTransport : ITransport
    {
        public ConcurrentQueue<string> Envelopes { get; } = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            Envelopes.Enqueue(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}