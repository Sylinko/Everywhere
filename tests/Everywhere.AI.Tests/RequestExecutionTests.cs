using Everywhere.Common;
using Everywhere.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Everywhere.AI.Tests;

/// <summary>
/// Exercises the production request executor and sessions through real HTTP and SDK streams.
/// </summary>
[Category("LlmIntegration")]
public sealed class RequestExecutionTests
{
    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.GeminiInteractions)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenTwoFailuresThenSuccess_ReplaysSameInputWithExactlyThreeSends(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new { statusCode = 503, body = "temporarily unavailable" }, times: 2);
        await server.CompleteAsync(provider, new { text = "recovered", streaming = true }, times: 1);
        using var session = CreateSession(provider, server.CreateClient(), 2);
        var history = new List<ChatMessage> { new(ChatRole.User, "fixed request") };
        var events = new List<ChatRequestUpdate>();
        await foreach (var update in session.StreamRequestAsync(history, session.CreateChatOptions())) events.Add(update);
        var requests = await server.RequestsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(requests, Has.Length.EqualTo(3));
            Assert.That(requests.Select(static request => request.GetProperty("body").GetRawText()), Is.All.EqualTo(requests[0].GetProperty("body").GetRawText()));
            Assert.That(events.OfType<ChatRequestUpdate.AttemptStarted>().Select(static start => start.AttemptNumber), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(events.OfType<ChatRequestUpdate.AttemptFailed>().ToArray(), Has.Length.EqualTo(2));
            Assert.That(events.Last(), Is.TypeOf<ChatRequestUpdate.Content>());
            Assert.That(history, Has.Count.EqualTo(1));
        });
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.GeminiInteractions)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenRetriesDisabled_HasOneSendAndTerminalDiagnostics(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new { statusCode = 503, body = "temporarily unavailable" });
        using var session = CreateSession(provider, server.CreateClient(), 0);
        var updates = new List<ChatRequestUpdate>();
        var error = await Assert.ThrowsAsync<ChatRequestException>(async () =>
        {
            await foreach (var update in session.StreamRequestAsync([new ChatMessage(ChatRole.User, "hello")], session.CreateChatOptions())) updates.Add(update);
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
    [TestCase(Provider.GeminiInteractions)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenCanceledDuringBackoff_DoesNotSendAgain(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new { statusCode = 503, body = "temporarily unavailable" });
        using var session = CreateSession(provider, server.CreateClient(), -1);
        using var cancellation = new CancellationTokenSource();
        await Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in session.StreamRequestAsync([new ChatMessage(ChatRole.User, "hello")], session.CreateChatOptions(), cancellationToken: cancellation.Token))
                if (update is ChatRequestUpdate.AttemptFailed) cancellation.Cancel();
        });
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.GeminiInteractions)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenConsumerLeavesAfterContent_DisposesWithoutAnotherAttempt(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.CompleteAsync(provider, new { text = "hello", streaming = true });
        using var session = CreateSession(provider, server.CreateClient(), 5);
        await foreach (var update in session.StreamRequestAsync([new ChatMessage(ChatRole.User, "hello")], session.CreateChatOptions()))
            if (update is ChatRequestUpdate.Content) break;
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.GeminiInteractions)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task StreamRequest_WhenRetryBudgetIsOne_PreservesBothFailuresAndStopsAtTwoSends(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new { statusCode = 503, body = "temporarily unavailable" });
        using var session = CreateSession(provider, server.CreateClient(), 1);
        var error = await Assert.ThrowsAsync<ChatRequestException>(async () =>
        {
            await foreach (var _ in session.StreamRequestAsync([new ChatMessage(ChatRole.User, "hello")], session.CreateChatOptions())) { }
        });
        Assert.Multiple(() =>
        {
            Assert.That(error?.TotalFailureCount, Is.EqualTo(2));
            Assert.That(error?.Failures.Select(static failure => failure.AttemptNumber), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(error?.FinalFailure, Is.InstanceOf<HandledChatException.ServiceUnavailable>());
        });
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(2));
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.GeminiInteractions)]
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
        using var session = CreateSession(provider, server.CreateClient(), -1);
        var error = await Assert.ThrowsAsync<ChatRequestException>(async () =>
        {
            await foreach (var _ in session.StreamRequestAsync([new ChatMessage(ChatRole.User, "hello")], session.CreateChatOptions())) { }
        });
        Assert.That(error?.FinalFailure, Is.InstanceOf<HandledChatException.InvalidRequest.ContextLengthExceeded>());
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    [TestCase(Provider.OpenAI, "length")]
    [TestCase(Provider.Anthropic, "max_tokens")]
    [TestCase(Provider.Gemini, "length")]
    [TestCase(Provider.GeminiInteractions, "length")]
    [TestCase(Provider.Mistral, "length")]
    public async Task StreamRequest_WhenOutputTruncated_FailsWithoutTransportReplay(Provider provider, string stopReason)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.CompleteAsync(provider, new { text = "partial", streaming = true, stopReason });
        using var session = CreateSession(provider, server.CreateClient(), 5);
        var error = await Assert.ThrowsAsync<ChatRequestException>(async () =>
        {
            await foreach (var _ in session.StreamRequestAsync([new ChatMessage(ChatRole.User, "hello")], session.CreateChatOptions())) { }
        });
        Assert.That(error?.FinalFailure, Is.InstanceOf(provider == Provider.GeminiInteractions
            ? typeof(HandledChatException.InvalidResponse.Incomplete) : typeof(HandledChatException.GenerationLimitExceeded)));
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    private static ModelSession CreateSession(Provider provider, HttpClient client, int maxRetries)
    {
        var schema = provider switch
        {
            Provider.OpenAIResponses => ModelProviderSchema.OpenAIResponses,
            Provider.Anthropic => ModelProviderSchema.Anthropic,
            Provider.Gemini => ModelProviderSchema.Google,
            Provider.GeminiInteractions => ModelProviderSchema.GoogleInteractions,
            Provider.Mistral => ModelProviderSchema.Mistral,
            Provider.Ollama => ModelProviderSchema.Ollama,
            _ => ModelProviderSchema.OpenAI
        };
        var configuration = new AdvancedAssistantConfiguration { Schema = schema, ModelId = "test-model" };
        var connection = new ModelConnection(schema, client.BaseAddress?.AbsoluteUri ?? throw new InvalidOperationException("Endpoint missing"), "test-key", client, null, maxRetries);
        return provider switch
        {
            Provider.OpenAI => new OpenAIModelSession(configuration, new OpenAIOptions(), connection, NullLoggerFactory.Instance),
            Provider.OpenAIResponses => new OpenAIResponsesModelSession(configuration, new OpenAIResponsesOptions(), connection, NullLoggerFactory.Instance),
            Provider.Anthropic => new AnthropicModelSession(configuration, new AnthropicOptions(), connection),
            Provider.Gemini => new GoogleModelSession(configuration, new GoogleOptions(), connection with { Endpoint = schema.NormalizeEndpoint(connection.Endpoint)! }),
            Provider.GeminiInteractions => new GoogleInteractionsModelSession(configuration, new GoogleInteractionsOptions(), connection with { Endpoint = schema.NormalizeEndpoint(connection.Endpoint)! }),
            Provider.Mistral => new MistralModelSession(configuration, new MistralOptions(), connection),
            _ => new OllamaModelSession(configuration, connection)
        };
    }
}
