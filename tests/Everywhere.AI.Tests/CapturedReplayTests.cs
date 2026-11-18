using System.Net;
using System.Text.Json;
using Everywhere.Common;
using Everywhere.Harness;
using Microsoft.Extensions.AI;

namespace Everywhere.AI.Tests;

/// <summary>
/// Replays upstream wire data through the production request executor, without contacting a model.
/// </summary>
[Category("LlmCaptured")]
public sealed class CapturedReplayTests
{
    private const string Gemini = "2026-11-18/google/gemini-3.5-flash-lite/";

    [TestCase(false)]
    [TestCase(true)]
    public async Task StreamRequest_WhenDeepSeekRejectsContext_RecoversContextWithoutRetry(bool isOfficial)
    {
        var capture = Load("2026-11-18/deepseek/deepseek-v4-pro/error-context-length-exceeded.json");
        await using var server = await MockServerSession.ConnectAsync();
        if (isOfficial)
        {
            // The capture is upstream HTTP 400. Only this envelope is reconstructed from the gateway contract.
            var envelope = new
            {
                success = false,
                error = new
                {
                    code = "upstream_error",
                    message = "The AI provider returned an error",
                    upstream = new { status = capture.Response.Status, body = JsonSerializer.Deserialize<JsonElement>(capture.Response.RawBody) }
                }
            };
            await server.RespondAsync(new
            {
                statusCode = 502,
                body = JsonSerializer.Serialize(envelope),
                headers = new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] }
            });
        }
        else
        {
            await capture.Response.ReplayAsync(server);
        }

        using var session = ProviderExtensions.CreateOpenAISession(server.CreateClient(), capture.ModelName, isOfficial);
        // The multi-megabyte synthetic request is omitted in the capture; only its rejection is under test.
        var error = await Assert.ThrowsAsync<ChatRequestException>(() =>
            ReadResponseAsync(session, [new ChatMessage(ChatRole.User, "context overflow probe")], session.CreateChatOptions()));
        var failure = error?.FinalFailure;
        var evidence = failure?.Diagnostics?.Evidence;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.TypeOf<HandledChatException.InvalidRequest.ContextLengthExceeded>());
            Assert.That(failure?.Recovery, Is.TypeOf<ChatExceptionRecovery.RecoverContext>());
            Assert.That(evidence?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(evidence?.GatewayStatusCode, Is.EqualTo(isOfficial ? HttpStatusCode.BadGateway : (HttpStatusCode?)null));
            Assert.That(evidence?.ErrorCode, Is.EqualTo("invalid_request_error"));
            Assert.That(evidence?.ErrorMessage, Does.Contain("maximum context length is 1048576 tokens"));
            Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
        }
    }

    [Test]
    public async Task StreamRequest_WhenGeminiAcceptsOversizedOutputLimit_CompletesWithCapturedUsage()
    {
        var capture = Load(Gemini + "error-interactions-max-tokens-exceeded.json");
        await using var server = await MockServerSession.ConnectAsync();
        await capture.Response.ReplayAsync(server);
        using var session = CreateGeminiSession(server, capture);
        var options = session.CreateChatOptions();
        options.MaxOutputTokens = 999999;
        var response = await ReadResponseAsync(session, [new ChatMessage(ChatRole.User, "hi")], options);
        var request = (await server.RequestsAsync()).Single();
        var body = MockServerSession.ReadRequestBody(request);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.GetProperty("path").GetString(), Is.EqualTo("/v1/interactions"));
            Assert.That(body.GetProperty("generation_config").GetProperty("max_output_tokens").GetInt32(), Is.EqualTo(999999));
            Assert.That(response.FinishReason, Is.EqualTo(ChatFinishReason.Stop));
            Assert.That(response.Text, Is.EqualTo("Hi there! How can I help you today?"));
            Assert.That(response.Usage?.InputTokenCount, Is.EqualTo(2));
            Assert.That(response.Usage?.OutputTokenCount, Is.EqualTo(10));
            Assert.That(response.Usage?.TotalTokenCount, Is.EqualTo(12));
        }
    }

    [Test]
    public async Task StreamRequest_WhenGeminiContinuesToolCall_ReplaysSignatureArgumentsAndPairedResult()
    {
        var first = Load(Gemini + "interactions-stream-tools-turn1.json");
        var second = Load(Gemini + "interactions-stream-tools-turn2.json");
        var input = first.Request.GetProperty("body");
        var tool = input.GetProperty("tools")[0];
        await using var server = await MockServerSession.ConnectAsync();
        using var session = CreateGeminiSession(server, first);
        var options = session.CreateChatOptions([AIFunctionFactory.CreateDeclaration(
            "lookup_status", tool.GetProperty("description").GetString(), tool.GetProperty("parameters"))]);
        var history = new List<ChatMessage> { new(ChatRole.User, input.GetProperty("input").GetString()) };
        await first.Response.ReplayAsync(server);
        var response = await ReadResponseAsync(session, history, options);
        Assert.That(response.FinishReason, Is.EqualTo(ChatFinishReason.ToolCalls));
        var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Single();
        Assert.That(call.Name, Is.EqualTo("lookup_status"));
        Assert.That(call.Arguments?["service"]?.ToString(), Is.EqualTo("cache"));

        // Only the simulated tool result is supplied locally. The assistant history comes from turn one.
        history.AddRange(response.Messages);
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, """{"status":"healthy","latencyMs":7}""")]));
        await second.Response.ReplayAsync(server);
        var final = await ReadResponseAsync(session, history, options);
        var requests = await server.RequestsAsync();
        Assert.That(requests, Has.Length.EqualTo(2));
        var body = MockServerSession.ReadRequestBody(requests[1]);
        var expectedInput = second.Request.GetProperty("body").GetProperty("input");
        var actualInput = body.GetProperty("input");
        // Compare JSON semantically: thought signature, call ID, arguments, tool result and step order.
        Assert.That(JsonElement.DeepEquals(actualInput, expectedInput), Is.True, "The outbound continuation differs from the captured input.");
        Assert.That(final.FinishReason, Is.EqualTo(ChatFinishReason.Stop));
        Assert.That(final.Text, Is.EqualTo("The `cache` service is currently **healthy** with a latency of **7 milliseconds**."));
    }

    private static Capture Load(string path)
    {
        var root = Environment.GetEnvironmentVariable("EVERYWHERE_LLM_FIXTURES");
        if (string.IsNullOrEmpty(root))
        {
            Assert.Ignore("Run tools/test_llm.ps1 -FixturesPath <llm-fixtures checkout> to replay captures.");
        }

        using var stream = File.OpenRead(Path.Combine(root, path));
        return JsonSerializer.Deserialize<Capture>(stream, JsonSerializerOptions.Web) ?? throw new InvalidDataException($"Empty capture: {path}");
    }

    private static GoogleInteractionsModelSession CreateGeminiSession(MockServerSession server, Capture capture)
    {
        const ModelProviderSchema Schema = ModelProviderSchema.GoogleInteractions;
        var configuration = new AdvancedAssistantConfiguration { Schema = Schema, ModelId = capture.ModelName };
        var endpoint = Schema.NormalizeEndpoint(server.Endpoint.AbsoluteUri) ?? throw new InvalidOperationException("Missing test endpoint.");
        return new GoogleInteractionsModelSession(configuration, new GoogleInteractionsOptions(),
            new ModelConnection(Schema, endpoint, "test-key", server.CreateClient(), null, 0));
    }

    private static async Task<ChatResponse> ReadResponseAsync(ModelSession session, List<ChatMessage> history, ChatOptions options)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in session.StreamRequestAsync(history, options))
        {
            if (update is ChatRequestUpdate.Content content) updates.Add(content.Value);
        }
        return updates.ToChatResponse();
    }

    private sealed record Capture(string ModelName, JsonElement Request, WireResponse Response);

    private sealed record WireResponse(int Status, Dictionary<string, string> Headers, string RawBody)
    {
        public Task ReplayAsync(MockServerSession server)
        {
            // rawBody is already decoded. Never forward compression, cookies, or gateway account IDs.
            var headers = new Dictionary<string, string[]> { ["Content-Type"] = [Headers["content-type"]] };
            if (Headers.TryGetValue("retry-after", out var delay)) headers.Add("Retry-After", [delay]);
            return server.RespondAsync(new
            {
                statusCode = Status,
                headers,
                body = new { type = "STRING", @string = RawBody, charset = "UTF-8" }
            }, times: 1);
        }
    }
}
