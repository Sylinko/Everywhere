using Everywhere.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI.Tests;

/// <summary>Exercises the production request executor and mixins through real HTTP and SDK streams.</summary>
[Category("LlmIntegration")]
public sealed class RequestExecutionTests
{
    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenTwoFailuresThenSuccess_ReplaysSameInputWithExactlyThreeSends(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new { statusCode = 503, body = "temporarily unavailable" }, times: 2);
        await server.CompleteAsync(provider, new { text = "recovered", streaming = true }, times: 1);
        using var mixin = CreateMixin(provider, server.CreateClient(), 2);
        var history = new ChatHistory();
        history.AddUserMessage("fixed request");
        var events = new List<ChatRequestUpdate>();
        await foreach (var update in mixin.StreamRequestAsync(history, mixin.GetPromptExecutionSettings())) events.Add(update);
        var requests = await server.RequestsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(requests, Has.Length.EqualTo(3));
            Assert.That(requests.Select(request => request.GetProperty("body").GetRawText()), Is.All.EqualTo(requests[0].GetProperty("body").GetRawText()));
            Assert.That(events.OfType<ChatRequestUpdate.AttemptStarted>().Select(start => start.AttemptNumber), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(events.OfType<ChatRequestUpdate.AttemptFailed>().ToArray(), Has.Length.EqualTo(2));
            Assert.That(events.Last(), Is.TypeOf<ChatRequestUpdate.Content>());
            Assert.That(history, Has.Count.EqualTo(1));
        });
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenRetriesDisabled_HasOneSendAndTerminalDiagnostics(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new { statusCode = 503, body = "temporarily unavailable" });
        using var mixin = CreateMixin(provider, server.CreateClient(), 0);
        var updates = new List<ChatRequestUpdate>();
        var error = Assert.ThrowsAsync<ChatRequestException>(async () =>
        {
            await foreach (var update in mixin.StreamRequestAsync([new(Microsoft.SemanticKernel.ChatCompletion.AuthorRole.User, "hello")], mixin.GetPromptExecutionSettings())) updates.Add(update);
        });
        Assert.Multiple(() =>
        {
            Assert.That(error?.TotalFailureCount, Is.EqualTo(1));
            Assert.That(error?.Failures, Has.Count.EqualTo(1));
            Assert.That(updates.Last(), Is.TypeOf<ChatRequestUpdate.AttemptFailed>());
            Assert.That(((ChatRequestUpdate.AttemptFailed)updates.Last()).RetryDelay, Is.Null);
        });
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenCanceledDuringBackoff_DoesNotSendAgain(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new { statusCode = 503, body = "temporarily unavailable" });
        using var mixin = CreateMixin(provider, server.CreateClient(), -1);
        using var cancellation = new CancellationTokenSource();
        Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in mixin.StreamRequestAsync([new(AuthorRole.User, "hello")], mixin.GetPromptExecutionSettings(), cancellationToken: cancellation.Token))
                if (update is ChatRequestUpdate.AttemptFailed) cancellation.Cancel();
        });
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenConsumerLeavesAfterContent_DisposesWithoutAnotherAttempt(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.CompleteAsync(provider, new { text = "hello", streaming = true });
        using var mixin = CreateMixin(provider, server.CreateClient(), 5);
        await foreach (var update in mixin.StreamRequestAsync([new(AuthorRole.User, "hello")], mixin.GetPromptExecutionSettings()))
            if (update is ChatRequestUpdate.Content) break;
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenRetryBudgetIsOne_PreservesBothFailuresAndStopsAtTwoSends(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new { statusCode = 503, body = "temporarily unavailable" });
        using var mixin = CreateMixin(provider, server.CreateClient(), 1);
        var error = Assert.ThrowsAsync<ChatRequestException>(async () =>
        {
            await foreach (var update in mixin.StreamRequestAsync([new(AuthorRole.User, "hello")], mixin.GetPromptExecutionSettings())) { }
        });
        Assert.Multiple(() =>
        {
            Assert.That(error?.TotalFailureCount, Is.EqualTo(2));
            Assert.That(error?.Failures.Select(failure => failure.AttemptNumber), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(error?.FinalFailure, Is.InstanceOf<HandledChatException.ServiceUnavailable>());
        });
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(2));
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenGatewayReportsContextOverflowWith503_DoesNotRetryUnchangedInput(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new
        {
            statusCode = 503,
            body = "{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"maximum context length exceeded\"}}",
            headers = new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] }
        });
        using var mixin = CreateMixin(provider, server.CreateClient(), -1);
        var error = Assert.ThrowsAsync<ChatRequestException>(async () =>
        {
            await foreach (var update in mixin.StreamRequestAsync([new(AuthorRole.User, "hello")], mixin.GetPromptExecutionSettings())) { }
        });
        Assert.That(error?.FinalFailure, Is.InstanceOf<HandledChatException.InvalidRequest.ContextLengthExceeded>());
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    [TestCase(Provider.OpenAI, "length")]
    [TestCase(Provider.Anthropic, "max_tokens")]
    [TestCase(Provider.Gemini, "length")]
    [TestCase(Provider.Mistral, "length")]
    public async Task StreamRequest_WhenOutputLimitIsExplicit_FailsWithoutTransportReplay(Provider provider, string stopReason)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.CompleteAsync(provider, new { text = "partial", streaming = true, stopReason });
        using var mixin = CreateMixin(provider, server.CreateClient(), 5);
        var error = Assert.ThrowsAsync<ChatRequestException>(async () =>
        {
            await foreach (var update in mixin.StreamRequestAsync([new(AuthorRole.User, "hello")], mixin.GetPromptExecutionSettings())) { }
        });
        Assert.That(error?.FinalFailure, Is.InstanceOf<HandledChatException.GenerationLimitExceeded>());
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    private static KernelMixin CreateMixin(Provider provider, HttpClient client, int maxRetries)
    {
        var schema = provider switch
        {
            Provider.OpenAIResponses => ModelProviderSchema.OpenAIResponses,
            Provider.Anthropic => ModelProviderSchema.Anthropic,
            Provider.Gemini => ModelProviderSchema.Google,
            Provider.Mistral => ModelProviderSchema.Mistral,
            Provider.Ollama => ModelProviderSchema.Ollama,
            _ => ModelProviderSchema.OpenAI
        };
        var configuration = new AdvancedAssistantConfiguration { Schema = schema, ModelId = "test-model" };
        var connection = new ModelConnection(schema, client.BaseAddress?.AbsoluteUri ?? throw new InvalidOperationException("Endpoint missing"), "test-key", client, null, maxRetries);
        return provider switch
        {
            Provider.OpenAI => new OpenAIKernelMixin(configuration, new OpenAIOptions(), connection, NullLoggerFactory.Instance),
            Provider.OpenAIResponses => new OpenAIResponsesKernelMixin(configuration, new OpenAIResponsesOptions(), connection, NullLoggerFactory.Instance),
            Provider.Anthropic => new AnthropicKernelMixin(configuration, new AnthropicOptions(), connection),
            Provider.Gemini => new GoogleKernelMixin(configuration, new GoogleOptions(), connection, NullLoggerFactory.Instance),
            Provider.Mistral => new MistralKernelMixin(configuration, new MistralOptions(), connection, NullLoggerFactory.Instance),
            _ => new OllamaKernelMixin(configuration, connection)
        };
    }
}
