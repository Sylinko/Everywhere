using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Everywhere.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using OllamaSharp.Models.Exceptions;

namespace Everywhere.AI.Tests;

public sealed class ExceptionClassificationTests
{
    [TestCaseSource(typeof(SdkErrorTests), nameof(SdkErrorTests.ErrorCases))]
    [Category("LlmIntegration")]
    public async Task Handle_WhenRealSdkRejects_ClassifiesEvidenceAndPreservesDiagnostics(Provider provider, ErrorFixture fixture)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new
        {
            statusCode = fixture.Status, body = new { type = "STRING", @string = fixture.WireBody, charset = "UTF-8" },
            headers = new Dictionary<string, string[]> { ["Content-Type"] = [(fixture.ContentType ?? "application/json") + "; charset=utf-8"], ["Retry-After"] = ["2"], ["request-id"] = [fixture.Id] }
        });
        using var client = server.CreateClient();
        var original = await SdkStreamingTests.CatchAsync(() => SdkStreamingTests.CollectAsync(provider.CreateService(client)));
        var handled = Normalize(original);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handled, Is.InstanceOf(ResolveExpectedType(fixture.ExpectedType)));
            Assert.That(handled.Recovery.GetType().Name, Is.EqualTo(fixture.ExpectedRecovery));
            Assert.That(handled.Diagnostics?.Evidence.StatusCode, Is.EqualTo((HttpStatusCode)fixture.Status));
            Assert.That(handled.Diagnostics?.Evidence.ResponseBody, Is.EqualTo(string.IsNullOrWhiteSpace(fixture.WireBody) ? null : fixture.WireBody));
            Assert.That(handled.Diagnostics?.Evidence.RequestId, Is.EqualTo(fixture.Id));
            Assert.That(handled.Diagnostics?.Evidence.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(handled.InnerException, Is.SameAs(original));
            Assert.That(ChatExceptionNormalizer.Handle(handled, null), Is.SameAs(handled));
            Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
        }
    }

    [Test]
    public void Handle_WhenSdkStatusIsZero_UsesInnerTransportCause()
    {
        var original = new ClientResultException("Service request failed.", null, new HttpRequestException("connection dropped"));
        var handled = Normalize(original);
        Assert.That(handled, Is.InstanceOf<HandledChatException.NetworkError>());
        Assert.That(handled.Recovery, Is.InstanceOf<ChatExceptionRecovery.Retry>());
        Assert.That(handled.Diagnostics?.Evidence.StatusCode, Is.Null);
    }

    [Test]
    public void Handle_WhenWrappedJsonFails_UsesInnerCauseWithoutReplacingOriginal()
    {
        var original = new KernelException("Unexpected response from model", new JsonException("wrong response shape"));
        var handled = Normalize(original);
        Assert.That(handled, Is.InstanceOf<HandledChatException.InvalidResponse.MalformedJson>());
        Assert.That(handled.InnerException, Is.SameAs(original));
        Assert.That(handled.Recovery, Is.InstanceOf<ChatExceptionRecovery.Stop>());
    }

    [Test]
    public void Handle_WhenOllamaRejectsTools_RecognizesSubtypeBeforeGenericError()
    {
        var handled = Normalize(new ModelDoesNotSupportToolsException("model does not support tools"));
        Assert.That(handled, Is.InstanceOf<HandledChatException.UnsupportedCapability>());
    }

    [Test]
    public void Handle_WhenCredentialsTlsFails_DoesNotTreatTransportAuthenticationAsApiKey()
    {
        var handled = Normalize(new HttpRequestException("SSL connection failed", new AuthenticationException("certificate rejected")));
        Assert.That(handled, Is.InstanceOf<HandledChatException.NetworkError.TlsError>());
        Assert.That(handled.Recovery, Is.InstanceOf<ChatExceptionRecovery.Stop>());
    }

    [Test]
    public void Handle_WhenCallerCancels_RecordsCancellationProvenance()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handled = Normalize(new OperationCanceledException(cancellation.Token), new ChatRequestFailureContext(cancellation.Token));
        Assert.That(handled.Diagnostics?.Evidence.IsCallerCancellation, Is.True);
        Assert.That(handled.Recovery, Is.InstanceOf<ChatExceptionRecovery.Canceled>());
    }

    [Test]
    public void Handle_WhenSdkTimeoutHasNoPhase_RetainsUncertainty()
    {
        var handled = Normalize(new TaskCanceledException("request timed out", new TimeoutException()));
        Assert.That(handled.Diagnostics?.Evidence.TimeoutPhase, Is.EqualTo(ChatRequestTimeoutPhase.Unknown));
        Assert.That(handled, Is.InstanceOf<HandledChatException.Timeout>());
    }

    [Test]
    public void Handle_WhenTokenIsCanceledAfterUnrelatedFailure_DoesNotEraseFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handled = Normalize(new JsonException("bad data"), new ChatRequestFailureContext(cancellation.Token));
        Assert.That(handled.Diagnostics?.Evidence.IsCallerCancellation, Is.False);
        Assert.That(handled, Is.InstanceOf<HandledChatException.InvalidResponse.MalformedJson>());
    }

    [TestCase("Unknown ChatFinishReason value.")]
    [TestCase("Unknown ReasoningStatus value.")]
    public void Handle_WhenSdkRejectsEnum_ReportsCompatibilityFailure(string message)
    {
        var handled = Normalize(new ArgumentOutOfRangeException("value", "new_value", message));
        Assert.That(handled, Is.InstanceOf<HandledChatException.InvalidResponse.UnsupportedFormat>());
        Assert.That(handled.Recovery, Is.InstanceOf<ChatExceptionRecovery.Stop>());
    }

    [Test]
    public void Handle_WhenErrorBodyEchoesRequest_DoesNotClassifyEchoedParameters()
    {
        var handled = Normalize(new HttpOperationException(HttpStatusCode.ServiceUnavailable,
            "{\"error\":{\"message\":\"upstream failed\"},\"request\":{\"prompt\":\"invalid api key\",\"temperature\":1}}", "service failed", null));
        Assert.That(handled, Is.InstanceOf<HandledChatException.ServiceUnavailable>());
    }

    [Test]
    public void Handle_WhenEvidenceBodyIsTooLarge_BoundsDiagnosticCopy()
    {
        var handled = Normalize(new HttpOperationException(HttpStatusCode.ServiceUnavailable, new string('x', 100000), "service failed", null));
        Assert.That(handled.Diagnostics?.Evidence.ResponseBody?.Length, Is.EqualTo(ChatExceptionEvidenceExtractor.MaximumBodyLength));
        Assert.That(handled.Recovery, Is.InstanceOf<ChatExceptionRecovery.Retry>());
    }

    [Test]
    public void Handle_WhenProxyRejectsTunnel_DoesNotTreatProxyStatusAsServiceParameterError()
    {
        var handled = Normalize(new HttpRequestException(HttpRequestError.ProxyTunnelError, "proxy rejected CONNECT", null, HttpStatusCode.BadRequest));
        Assert.That(handled, Is.InstanceOf<HandledChatException.NetworkError>());
        Assert.That(handled.Recovery, Is.InstanceOf<ChatExceptionRecovery.Stop>());
        Assert.That(handled.Diagnostics?.RuleId, Is.EqualTo("proxy-tunnel-rejected"));
    }

    [TestCase("NaN")]
    public void Handle_WhenRetryHeaderIsInvalid_IgnoresItWithoutMaskingFailure(string value)
    {
        var original = new HttpOperationException(HttpStatusCode.ServiceUnavailable, "upstream unavailable", "failed", null);
        original.Data["Everywhere.Http.Retry-After"] = value;
        var handled = Normalize(original);
        Assert.That(handled.Diagnostics?.Evidence.RetryAfter, Is.Null);
        Assert.That(handled.Recovery, Is.InstanceOf<ChatExceptionRecovery.Retry>());
    }

    [Test]
    public void FromErrorCode_WhenStreamReportsContextOverflow_UsesSharedClassification()
    {
        var original = new Exception("context_length_exceeded");
        var handled = ChatExceptionNormalizer.FromErrorCode(original, "context_length_exceeded");
        Assert.That(handled, Is.InstanceOf<HandledChatException.InvalidRequest.ContextLengthExceeded>());
        Assert.That(handled.Recovery, Is.InstanceOf<ChatExceptionRecovery.RecoverContext>());
        Assert.That(handled.InnerException, Is.SameAs(original));
    }

    [Test]
    public void FromErrorCode_WhenGenericServerErrorHasNoHttpStatus_UsesCodeBaseline()
    {
        var handled = ChatExceptionNormalizer.FromErrorCode(new Exception("upstream failed"), "server_error");
        Assert.That(handled, Is.InstanceOf<HandledChatException.ServiceUnavailable>());
        Assert.That(handled.Recovery, Is.InstanceOf<ChatExceptionRecovery.Retry>());
        Assert.That(handled.Diagnostics?.Source, Is.EqualTo("code"));
    }

    [Test]
    public void Handle_WhenOfficialGatewayWrapsUpstream_UsesUpstreamAndRetainsGatewayStatus()
    {
        using var mixin = CreateOfficialMixin();
        var original = new HttpOperationException(HttpStatusCode.BadGateway,
            "{\"success\":false,\"error\":{\"code\":\"upstream_error\",\"upstream\":{\"status\":503,\"body\":{\"error\":{\"message\":\"Invalid API key provided\"}}}}}", "upstream_error", null);
        var handled = (HandledChatException)ChatExceptionNormalizer.Handle(original, mixin);
        Assert.That(handled, Is.InstanceOf<HandledChatException.AuthenticationFailure.InvalidApiKey>());
        Assert.That(handled.Diagnostics?.Evidence.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(handled.Diagnostics?.Evidence.GatewayStatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        Assert.That(handled.InnerException, Is.SameAs(original));
    }

    [Test]
    public void Handle_WhenOfficialCodeIsKnown_PreservesConnectionSemanticsAndFriendlyKey()
    {
        using var mixin = CreateOfficialMixin();
        var original = new HttpOperationException(HttpStatusCode.ServiceUnavailable,
            "{\"success\":false,\"error\":{\"code\":\"billing_insufficient_credits\",\"message\":\"raw diagnostic marker\"}}", "gateway failed", null);
        var handled = (HandledChatException)ChatExceptionNormalizer.Handle(original, mixin);
        Assert.That(handled, Is.InstanceOf<HandledChatException.QuotaExceeded>());
        Assert.That(handled.Diagnostics?.Source, Is.EqualTo("connection"));
        Assert.That(handled.Diagnostics?.Evidence.ErrorMessage, Is.EqualTo("raw diagnostic marker"));
        Assert.That(handled.FriendlyMessageKey, Is.SameAs(handled.Diagnostics?.Evidence.FriendlyMessageKey));
        Assert.That(ChatExceptionNormalizer.Handle(handled, mixin), Is.SameAs(handled));
    }

    [TestCaseSource(nameof(ObservedMessages))]
    public void Handle_WhenOnlySentryMessageIsAvailable_DoesNotInventMissingEvidence(ObservedErrorFixture fixture)
    {
        var handled = Normalize(new Exception(fixture.ErrorMessage));
        Assert.That(handled, Is.InstanceOf(ResolveExpectedType(fixture.ExpectedType)));
        Assert.That(handled.Diagnostics?.Evidence.StatusCode, Is.Null);
        Assert.That(handled.Diagnostics?.Evidence.OriginalException.Message, Is.EqualTo(fixture.ErrorMessage));
    }

    public static IEnumerable<TestCaseData> ObservedMessages()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Observed", "sentry-messages.json");
        var fixtures = JsonSerializer.Deserialize(File.ReadAllText(path), FixtureJsonContext.Default.ObservedErrorFixtureArray)
            ?? throw new InvalidDataException("Observed message fixtures are required.");
        foreach (var fixture in fixtures) yield return new TestCaseData(fixture).SetName($"Observed_{fixture.ShortId}");
    }

    private static KernelMixin CreateOfficialMixin()
    {
        // Release builds intentionally omit the private deployment's gateway URL.
        // Bind the real connection adapter without changing build constants or sending HTTP.
        var method = typeof(KernelMixinFactory).GetMethod("EnrichOfficialExceptionEvidence", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new MissingMethodException("Official evidence adapter is missing.");
        var enrich = method.CreateDelegate<Action<ChatExceptionEvidence>>();
        var connection = new ModelConnection(ModelProviderSchema.OpenAI, "https://test.invalid/v1", "test-key", new HttpClient(), enrich);
        return new OpenAIKernelMixin(new AdvancedAssistantConfiguration { Schema = ModelProviderSchema.OpenAI, ModelId = "test-model" },
            new OpenAIOptions(), connection, NullLoggerFactory.Instance);
    }

    private static Type ResolveExpectedType(string name) =>
        typeof(HandledChatException).Assembly.GetType(typeof(HandledChatException).FullName + "+" + name.Replace('.', '+'))
            ?? throw new InvalidDataException($"Unknown expected exception type: {name}");

    private static HandledChatException Normalize(Exception original, ChatRequestFailureContext context = default) =>
        (HandledChatException)ChatExceptionNormalizer.Handle(original, null, context);

}
