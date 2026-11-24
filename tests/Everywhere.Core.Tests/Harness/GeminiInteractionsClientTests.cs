using System.Net;
using System.Text;
using System.Text.Json;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Harness;
using Everywhere.Harness.Providers.Gemini;
using Tabler.Avalonia;
using MessagePack;
using Microsoft.Extensions.AI;
using NSubstitute;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Everywhere.Core.Tests.Harness;

public sealed class GeminiInteractionsClientTests
{
    private static readonly Uri Endpoint = new("https://fixture.invalid/v1/interactions");

    [TestCase("https://example.invalid", "https://example.invalid/v1/interactions")]
    [TestCase("https://example.invalid/v1/interactions", "https://example.invalid/v1/interactions")]
    [TestCase("https://example.invalid/v1beta/", "https://example.invalid/v1beta/interactions")]
    [TestCase("https://example.invalid/proxy/v1/interactions?q=test", "https://example.invalid/proxy/v1/interactions?q=test")]
    [TestCase("https://example.invalid/custom/interactions#", "https://example.invalid/custom/interactions")]
    public void PreviewEndpoint_WithStoredOrCustomBase_ResolvesOneInteractionsResource(string stored, string expected)
    {
        Assert.That(ModelProviderSchema.GoogleInteractions.PreviewEndpoint(stored), Is.EqualTo(expected));
    }

    [Test]
    public async Task Streaming_WhenInlineImageReturned_ExposesDataContent()
    {
        using var wire = new WireHandler([
            """{"event_type":"step.start","index":0,"step":{"type":"model_output","content":[{"type":"image","mime_type":"image/png","data":"AQID"}]}}""",
            """{"event_type":"step.stop","index":0}""",
            """{"event_type":"interaction.completed","interaction":{"id":"image","status":"completed"}}"""
        ]);
        using var session = new GoogleInteractionsModelSession(
            new AdvancedAssistantConfiguration { ModelId = "fixture" },
            new GoogleInteractionsOptions(),
            new ModelConnection(ModelProviderSchema.GoogleInteractions, "https://fixture.invalid/v1", "synthetic-key", new HttpClient(wire, false), null));
        var updates = await session.ChatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Show the image.")]).ToListAsync();
        var image = updates.SelectMany(static update => update.Contents).OfType<DataContent>().Single();
        Assert.That(image.MediaType, Is.EqualTo("image/png"));
        Assert.That(image.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public async Task GoogleInteractionsOptions_WhenSamplingAndThinkingConfigured_SendsGenerationConfig()
    {
        using var wire = new WireHandler([
            """{"event_type":"step.start","index":0,"step":{"type":"model_output","content":[{"type":"text","text":"ok"}]}}""",
            """{"event_type":"step.stop","index":0}""",
            """{"event_type":"interaction.completed","interaction":{"id":"cfg","status":"completed"}}"""
        ]);
        var options = new GoogleInteractionsOptions
        {
            Temperature = "0.4",
            TopP = "0.85",
            ReasoningEffort = "high",
            IncludeThoughts = true
        };
        using var session = new GoogleInteractionsModelSession(
            new AdvancedAssistantConfiguration { ModelId = "gemini-2.5-pro" },
            options,
            new ModelConnection(ModelProviderSchema.GoogleInteractions, "https://fixture.invalid/v1", "synthetic-key", new HttpClient(wire, false), null));
        await session.ChatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]).ToListAsync();

        var genConfig = wire.Requests.Single().GetProperty("generation_config");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(genConfig.GetProperty("temperature").GetSingle(), Is.EqualTo(0.4f).Within(0.001f));
            Assert.That(genConfig.GetProperty("top_p").GetSingle(), Is.EqualTo(0.85f).Within(0.001f));
            Assert.That(genConfig.TryGetProperty("top_k", out _), Is.False);
            Assert.That(genConfig.TryGetProperty("thinking_budget", out _), Is.False);
            Assert.That(genConfig.GetProperty("thinking_level").GetString(), Is.EqualTo("high"));
            Assert.That(genConfig.GetProperty("thinking_summaries").GetString(), Is.EqualTo("auto"));
        }
    }

    [Test]
    public async Task SignedToolTurn_WhenPersistedInSpansAndReopened_ReplaysNativeStepsAndDegradesForeignCalls()
    {
        using var handler = new WireHandler();
        using var http = new HttpClient(handler);
        using var writer = new GeminiInteractionsClient(http, Endpoint, "fixture", "synthetic-key");
        var question = new ChatMessage(ChatRole.User, "What's the weather?");

        // Assemble the streaming turn into spans exactly as ChatService does.
        using var assistant = new AssistantChatMessage();
        var assembler = new ResponseAssembler(
            GeminiInteractionsClient.DialectName,
            openVisibleBuffer: (isReasoning, block) =>
            {
                if (isReasoning)
                {
                    var rs = block.BoundSpan as AssistantChatMessageReasoningSpan ?? new AssistantChatMessageReasoningSpan();
                    if (block.BoundSpan is null) assistant.AddSpan(rs);
                    block.BoundSpan = rs;
                    return rs.ReasoningMarkdownBuilder;
                }

                var ts = block.BoundSpan as AssistantChatMessageTextSpan ?? new AssistantChatMessageTextSpan();
                if (block.BoundSpan is null) assistant.AddSpan(ts);
                block.BoundSpan = ts;
                return ts.ContentMarkdownBuilder;
            },
            onBlockUpdated: block =>
            {
                if (block.BoundSpan is null && block.IsReasoning && block.Metadata.Count > 0)
                {
                    var rs = new AssistantChatMessageReasoningSpan();
                    assistant.AddSpan(rs);
                    block.BoundSpan = rs;
                }

                if (block.BoundSpan is { } bound && block.Metadata.Count > 0)
                {
                    bound.Metadata = block.Metadata;
                }
            });

        ChatResponseUpdate? terminal = null;
        await foreach (var update in writer.GetStreamingResponseAsync([question]))
        {
            assembler.AppendUpdate(update);
            terminal = update;
        }

        var call = assembler.FunctionCalls.Single();
        Assert.That(terminal?.FinishReason, Is.EqualTo(ChatFinishReason.ToolCalls));
        Assert.That(terminal?.Contents.OfType<UsageContent>().Single().Details.OutputTokenCount, Is.EqualTo(18));

        var toolMsg = new FunctionCallChatMessage(TablerIconKind.Hammer, null);
        toolMsg.AddCall(new ToolCallRecord(call.Name, null, call.CallId, call.Arguments?.ToDictionary())
        {
            Metadata = new Dictionary<string, object?> { [ReasoningMetadata.DialectKey] = GeminiInteractionsClient.DialectName }
        });
        toolMsg.AddResult(new ToolResultRecord(call.Name, null, call.CallId, "sunny"));
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(toolMsg)
        {
            Metadata = MetadataDictionary.FromDictionary(new Dictionary<string, object?> { [ReasoningMetadata.DialectKey] = GeminiInteractionsClient.DialectName })
        });
        assistant.AddSpan(new AssistantChatMessageTextSpan("continued"));

        // Serialize and deserialize through MessagePack to verify durable span persistence.
        var payload = MessagePackSerializer.Serialize<Everywhere.Chat.ChatMessage>(assistant);
        using var reopened = (AssistantChatMessage)MessagePackSerializer.Deserialize<Everywhere.Chat.ChatMessage>(payload);

        using var reader = new GeminiInteractionsClient(http, Endpoint, "another-model", "synthetic-key");
        var selected = new Everywhere.Chat.ChatMessage[] { new UserChatMessage(question.Text, []), reopened };
        var captured = await ChatHistoryBuilder.CaptureModelInputAsync(
            Substitute.For<IPromptRenderer>(),
            string.Empty,
            selected,
            Modalities.Text,
            null,
            CancellationToken.None,
            null,
            GeminiInteractionsClient.DialectName,
            supportsToolResultBlocks: true);

        var answer = await reader.GetResponseAsync(captured.History);
        Assert.That(answer.Text, Is.EqualTo("Sunny."));

        var request = handler.Requests[1];
        var steps = request.GetProperty("input");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.GetProperty("model").GetString(), Is.EqualTo("another-model"));
            Assert.That(request.GetProperty("store").GetBoolean(), Is.False);
            Assert.That(request.TryGetProperty("previous_interaction_id", out _), Is.False);
            Assert.That(steps.EnumerateArray().Select(static step => step.GetProperty("type").GetString()),
                Is.EqualTo(new[] { "user_input", "model_output", "thought", "google_search_call", "google_search_result", "function_call", "function_result", "model_output" }));
            Assert.That(steps[2].GetProperty("signature").GetString(), Is.EqualTo("private-thought"));
            Assert.That(steps[3].GetProperty("signature").GetString(), Is.EqualTo("search-sig"));
            Assert.That(steps[4].GetProperty("call_id").GetString(), Is.EqualTo("hosted"));
            Assert.That(steps[5].GetProperty("arguments").GetProperty("city").GetString(), Is.EqualTo("Paris"));
            Assert.That(steps[6].GetProperty("call_id").GetString(), Is.EqualTo("local-call"));
            Assert.That(steps[6].GetProperty("name").GetString(), Is.EqualTo("weather"));
            Assert.That(steps[6].GetProperty("result").GetString(), Is.EqualTo("sunny"));
            Assert.That(steps[7].GetProperty("content")[0].GetProperty("text").GetString(), Is.EqualTo("continued"));
        }

        // Foreign provider tool turn (e.g. OpenAI) must degrade to portable evidence instead of native function_call without signature.
        using var foreignAssistant = new AssistantChatMessage();
        var foreignTool = new FunctionCallChatMessage(TablerIconKind.Hammer, null);
        foreignTool.AddCall(new ToolCallRecord("weather", null, "openai-call", new Dictionary<string, object?> { ["city"] = "Berlin" })
        {
            Metadata = new Dictionary<string, object?> { [ReasoningMetadata.DialectKey] = OpenAIModelSession.DialectName }
        });
        foreignTool.AddResult(new ToolResultRecord("weather", null, "openai-call", "cloudy"));
        foreignAssistant.AddSpan(new AssistantChatMessageFunctionCallSpan(foreignTool)
        {
            Metadata = MetadataDictionary.FromDictionary(new Dictionary<string, object?> { [ReasoningMetadata.DialectKey] = OpenAIModelSession.DialectName })
        });
        var foreignInput = await ChatHistoryBuilder.CaptureModelInputAsync(
            Substitute.For<IPromptRenderer>(),
            string.Empty,
            [new UserChatMessage("Berlin?", []), foreignAssistant],
            Modalities.Text,
            null,
            CancellationToken.None,
            null,
            GeminiInteractionsClient.DialectName,
            supportsToolResultBlocks: true);
        await reader.GetResponseAsync(foreignInput.History);
        var degradedSteps = handler.Requests[2].GetProperty("input");
        Assert.That(degradedSteps.EnumerateArray().Select(static s => s.GetProperty("type").GetString()),
            Is.All.Matches<string>(static t => t is "user_input" or "model_output"));
        Assert.That(degradedSteps.GetRawText(), Does.Contain("prior_tool_call").And.Contain("prior_tool_result").And.Contain("cloudy"));
    }

    [Test]
    public async Task Streaming_WhenConnectionEndsInsideToolArguments_DoesNotReleaseIncompleteCall()
    {
        var frames = new[]
        {
            """{"event_type":"step.start","index":0,"step":{"type":"model_output","content":[{"type":"text","text":"Checking."}]}}""",
            """{"event_type":"step.stop","index":0}""",
            """{"event_type":"step.start","index":1,"step":{"type":"function_call","id":"incomplete","name":"weather","arguments":{}}}""",
            """{"event_type":"step.delta","index":1,"delta":{"type":"arguments_delta","arguments":"{}"}}"""
        };
        using var http = new HttpClient(new WireHandler(frames));
        using var client = new GeminiInteractionsClient(http, Endpoint, "fixture", "synthetic-key");
        var updates = new List<ChatResponseUpdate>();
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "weather?")])) updates.Add(update);
        });
        Assert.That(updates.SelectMany(static update => update.Contents).OfType<TextContent>().Single().Text, Is.EqualTo("Checking."));
        Assert.That(updates.SelectMany(static update => update.Contents).OfType<FunctionCallContent>(), Is.Empty);
    }

    [Test]
    public async Task CheckConnectivity_WhenStatelessServerOmitsInteractionIdAndSendsDoneFrame_Succeeds()
    {
        const string sse =
            "data: {\"event_type\":\"interaction.created\",\"interaction\":{\"model\":\"gemini-2.5-flash\",\"status\":\"in_progress\"}}\n\n" +
            "data: {\"event_type\":\"step.start\",\"index\":0,\"step\":{\"type\":\"model_output\"}}\n\n" +
            "data: {\"event_type\":\"step.delta\",\"index\":0,\"delta\":{\"type\":\"text\",\"text\":\"OK\"}}\n\n" +
            "data: {\"event_type\":\"step.stop\",\"index\":0}\n\n" +
            "data: {\"event_type\":\"step.start\",\"index\":1,\"step\":{\"type\":\"function_call\",\"name\":\"ping\"}}\n\n" +
            "data: {\"event_type\":\"step.stop\",\"index\":1}\n\n" +
            "data: {\"event_type\":\"interaction.completed\",\"interaction\":{\"status\":\"requires_action\",\"usage\":{\"total_input_tokens\":5,\"total_output_tokens\":2,\"total_thought_tokens\":null}}}\n\n" +
            "event: done\ndata: [DONE]\n\n";
        using var handler = new RawSseHandler(sse);
        using var session = new GoogleInteractionsModelSession(
            new AdvancedAssistantConfiguration { Schema = ModelProviderSchema.GoogleInteractions, ModelId = "gemini-2.5-flash" },
            new GoogleInteractionsOptions(),
            new ModelConnection(ModelProviderSchema.GoogleInteractions, "https://fixture.invalid/v1", "synthetic-key", new HttpClient(handler, false), null));

        await session.CheckConnectivityAsync();

        var updates = await session.ChatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();
        var text = string.Concat(updates.Select(static u => u.Text));
        var call = updates.SelectMany(static u => u.Contents).OfType<FunctionCallContent>().Single();
        var usage = updates.SelectMany(static u => u.Contents).OfType<UsageContent>().Single().Details;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Is.EqualTo("OK"));
            Assert.That(call.Name, Is.EqualTo("ping"));
            Assert.That(call.CallId, Is.Not.Empty);
            Assert.That(call.Arguments, Is.Empty);
            Assert.That(usage.InputTokenCount, Is.EqualTo(5));
            Assert.That(usage.OutputTokenCount, Is.EqualTo(2));
        }
    }

    private sealed class RawSseHandler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
    }

    private sealed class WireHandler(string[]? streamFrames = null) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.That(request.RequestUri, Is.EqualTo(Endpoint));
            Assert.That(request.Headers.GetValues("x-goog-api-key").Single(), Is.EqualTo("synthetic-key"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Requests.Add(body.RootElement.Clone());
            var isStream = body.RootElement.GetProperty("stream").GetBoolean();
            var frames = streamFrames ??
            [
                """{"event_type":"interaction.created","interaction":{"id":"int-1","model":"fixture"}}""",
                """{"event_type":"step.start","index":0,"step":{"type":"model_output"}}""",
                """{"event_type":"step.delta","index":0,"delta":{"type":"text","text":"public-before"}}""",
                """{"event_type":"step.stop","index":0}""",
                """{"event_type":"step.start","index":1,"step":{"type":"thought"}}""",
                """{"event_type":"step.delta","index":1,"delta":{"type":"thought_signature","signature":"private-thought"}}""",
                """{"event_type":"step.stop","index":1}""",
                """{"event_type":"step.start","index":2,"step":{"type":"google_search_call","id":"hosted"}}""",
                """{"event_type":"step.delta","index":2,"delta":{"type":"google_search_call","arguments":{"queries":["weather"]},"signature":"search-sig"}}""",
                """{"event_type":"step.stop","index":2}""",
                """{"event_type":"step.start","index":3,"step":{"type":"google_search_result","call_id":"hosted"}}""",
                """{"event_type":"step.stop","index":3}""",
                """{"event_type":"step.start","index":4,"step":{"type":"function_call","id":"local-call","name":"weather","arguments":{}}}""",
                """{"event_type":"step.delta","index":4,"delta":{"type":"arguments_delta","arguments":"{\"city\":"}}""",
                """{"event_type":"step.delta","index":4,"delta":{"type":"arguments_delta","arguments":"\"Paris\"}"}}""",
                """{"event_type":"step.stop","index":4}""",
                """{"event_type":"interaction.completed","interaction":{"id":"int-1","status":"requires_action","usage":{"total_tokens":29,"total_input_tokens":11,"total_output_tokens":8,"total_thought_tokens":10}}}"""
            ];
            var content = isStream ? string.Concat(frames.Select(static frame => $"data: {frame}\n\n")) :
                """{"id":"int-2","model":"fixture","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"Sunny."}]}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, isStream ? "text/event-stream" : "application/json")
            };
        }
    }
}
