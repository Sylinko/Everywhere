using System.Net;
using System.Text;
using System.Text.Json;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Harness;
using Everywhere.Harness.Providers.Gemini;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.Extensions.AI;
using NSubstitute;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Everywhere.Core.Tests.Harness;

public sealed class GeminiGenerateContentClientTests
{
    private static readonly Uri BaseUri = new("https://fixture.invalid/v1beta");

    [TestCase("https://example.invalid", "https://example.invalid/v1beta")]
    [TestCase("https://example.invalid/v1/", "https://example.invalid/v1")]
    [TestCase("https://example.invalid/v1beta/", "https://example.invalid/v1beta")]
    [TestCase("https://example.invalid/v1beta/models", "https://example.invalid/v1beta")]
    [TestCase("https://example.invalid/custom#", "https://example.invalid/custom")]
    public void NormalizeEndpoint_PreservesExplicitVersionAndDefaultsToV1Beta(string stored, string expected)
    {
        Assert.That(ModelProviderSchema.Google.NormalizeEndpoint(stored), Is.EqualTo(expected));
    }

    [TestCase("https://example.invalid", "https://example.invalid/v1beta/models/{model}:generateContent")]
    [TestCase("https://example.invalid/v1", "https://example.invalid/v1/models/{model}:generateContent")]
    [TestCase("https://example.invalid/v1beta", "https://example.invalid/v1beta/models/{model}:generateContent")]
    [TestCase("https://example.invalid/v1/models/gemini-2.5-pro:generateContent#", "https://example.invalid/v1/models/gemini-2.5-pro:generateContent")]
    public void PreviewEndpoint_AppendsGenerateContentResourceUnlessExplicit(string stored, string expected)
    {
        Assert.That(ModelProviderSchema.Google.PreviewEndpoint(stored), Is.EqualTo(expected));
    }

    [TestCase(ModelProviderSchema.OpenAI, "https://example.invalid/v1/chat/completions", "https://example.invalid/v1", "https://example.invalid/v1/chat/completions")]
    [TestCase(ModelProviderSchema.OpenAI, "https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    [TestCase(ModelProviderSchema.OpenAI, "https://example.invalid/custom/chat/completions#", "https://example.invalid/custom/chat/completions", "https://example.invalid/custom/chat/completions")]
    [TestCase(ModelProviderSchema.Anthropic, "https://api.anthropic.com/v1", "https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    [TestCase(ModelProviderSchema.Anthropic, "https://api.minimax.io/anthropic", "https://api.minimax.io/anthropic", "https://api.minimax.io/anthropic/v1/messages")]
    [TestCase(ModelProviderSchema.Anthropic, "https://example.invalid/v1#", "https://example.invalid/v1", "https://example.invalid/v1/v1/messages")]
    [TestCase(ModelProviderSchema.Mistral, "https://api.mistral.ai/v1/chat/completions", "https://api.mistral.ai/v1", "https://api.mistral.ai/v1/chat/completions")]
    public void EndpointRules_AdaptPastedSuffixesWhilePreservingCustomSubpathsAndRawHash(
        ModelProviderSchema schema,
        string input,
        string expectedNormalized,
        string expectedPreview)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schema.NormalizeEndpoint(input), Is.EqualTo(expectedNormalized));
            Assert.That(schema.PreviewEndpoint(input), Is.EqualTo(expectedPreview));
        }
    }

    [Test]
    public async Task GoogleOptions_WhenSamplingAndThinkingConfigured_SendsGenerationConfigAndTransformsTools()
    {
        using var wire = new WireHandler([
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}"""
        ]);
        var options = new GoogleOptions
        {
            Temperature = "0.4",
            TopP = "0.85",
            TopK = "32",
            ThinkingBudget = "4096",
            ReasoningEffort = "high",
            IncludeThoughts = true
        };
        using var session = new GoogleModelSession(
            new AdvancedAssistantConfiguration { ModelId = "gemini-2.5-pro", Schema = ModelProviderSchema.Google },
            options,
            new ModelConnection(ModelProviderSchema.Google, "https://fixture.invalid/v1beta", "synthetic-key", new HttpClient(wire, false), null));
        var toolSchema = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""");
        var tool = AIFunctionFactory.CreateDeclaration("weather", "Get weather", toolSchema);

        await session.ChatClient.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            session.CreateChatOptions([tool], ChatToolMode.Auto)).ToListAsync();

        var request = wire.Requests.Single();
        var genConfig = request.GetProperty("generationConfig");
        var thinkingConfig = genConfig.GetProperty("thinkingConfig");
        var declaration = request.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wire.RequestUris.Single().AbsoluteUri, Is.EqualTo("https://fixture.invalid/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse"));
            Assert.That(genConfig.GetProperty("temperature").GetSingle(), Is.EqualTo(0.4f).Within(0.001f));
            Assert.That(genConfig.GetProperty("topP").GetSingle(), Is.EqualTo(0.85f).Within(0.001f));
            Assert.That(genConfig.GetProperty("topK").GetInt32(), Is.EqualTo(32));
            Assert.That(thinkingConfig.GetProperty("thinkingBudget").GetInt32(), Is.EqualTo(4096));
            Assert.That(thinkingConfig.GetProperty("thinkingLevel").GetString(), Is.EqualTo("high"));
            Assert.That(thinkingConfig.GetProperty("includeThoughts").GetBoolean(), Is.True);
            Assert.That(declaration.GetProperty("name").GetString(), Is.EqualTo("weather"));
            Assert.That(declaration.GetProperty("parameters").TryGetProperty("additionalProperties", out _), Is.False);
        }
    }

    [Test]
    public async Task GoogleOptions_WhenIncludeThoughtsDisabledWithNoBudgetOrLevel_OmitsThinkingConfig()
    {
        using var wire = new WireHandler([
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}"""
        ]);
        using var session = new GoogleModelSession(
            new AdvancedAssistantConfiguration { ModelId = "gemini-2.0-flash", Schema = ModelProviderSchema.Google },
            new GoogleOptions { IncludeThoughts = false },
            new ModelConnection(ModelProviderSchema.Google, "https://fixture.invalid/v1", "synthetic-key", new HttpClient(wire, false), null));

        await session.ChatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]).ToListAsync();

        var request = wire.Requests.Single();
        Assert.That(request.TryGetProperty("generationConfig", out var genConfig) && genConfig.TryGetProperty("thinkingConfig", out _), Is.False);
    }

    [Test]
    public async Task SignedToolTurn_WhenPersistedInSpansAndReopened_ReplaysCallThoughtSignatureAndDegradesForeignCalls()
    {
        using var handler = new WireHandler();
        using var http = new HttpClient(handler);
        using var writer = new GeminiGenerateContentClient(http, BaseUri, "gemini-2.5-pro", "synthetic-key");
        var question = new ChatMessage(ChatRole.User, "What's the weather?");

        var assistant = new AssistantChatMessage();
        try
        {
            var assembler = new ResponseAssembler(
                GeminiGenerateContentClient.DialectName,
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
                    if (block is { BoundSpan: null, IsReasoning: true, Metadata.Count: > 0 })
                    {
                        var rs = new AssistantChatMessageReasoningSpan();
                        assistant.AddSpan(rs);
                        block.BoundSpan = rs;
                    }

                    if (block is { BoundSpan: { } bound, Metadata.Count: > 0 })
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(terminal?.FinishReason, Is.EqualTo(ChatFinishReason.ToolCalls));
                Assert.That(terminal?.Contents.OfType<UsageContent>().Single().Details.OutputTokenCount, Is.EqualTo(18));
                Assert.That(call.AdditionalProperties?[ReasoningMetadata.ThoughtSignatureKey], Is.EqualTo("call-sig"));
            }

            var callMetadata = new Dictionary<string, object?>
            {
                [ReasoningMetadata.DialectKey] = GeminiGenerateContentClient.DialectName,
                [ReasoningMetadata.ThoughtSignatureKey] = call.AdditionalProperties?[ReasoningMetadata.ThoughtSignatureKey]
            };
            var toolMsg = new FunctionCallChatMessage(LucideIconKind.Hammer, null);
            toolMsg.AddCall(new ToolCallRecord(call.Name, null, call.CallId, call.Arguments?.ToDictionary())
            {
                Metadata = callMetadata
            });
            toolMsg.AddResult(new ToolResultRecord(call.Name, null, call.CallId, "sunny"));
            assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(toolMsg)
            {
                Metadata = MetadataDictionary.FromDictionary(new Dictionary<string, object?> { [ReasoningMetadata.DialectKey] = GeminiGenerateContentClient.DialectName })
            });
            assistant.AddSpan(new AssistantChatMessageTextSpan("continued"));

            var payload = MessagePackSerializer.Serialize<Everywhere.Chat.ChatMessage>(assistant);
            using var reopened = (AssistantChatMessage)MessagePackSerializer.Deserialize<Everywhere.Chat.ChatMessage>(payload);

            using var reader = new GeminiGenerateContentClient(http, BaseUri, "gemini-3.5-flash", "synthetic-key");
            var selected = new Everywhere.Chat.ChatMessage[] { new UserChatMessage(question.Text, []), reopened };
            var captured = await ChatHistoryBuilder.CaptureModelInputAsync(
                Substitute.For<IPromptRenderer>(),
                "Be concise.",
                selected,
                Modalities.Text,
                null,
                CancellationToken.None,
                null,
                GeminiGenerateContentClient.DialectName,
                supportsToolResultBlocks: false);

            var answer = await reader.GetResponseAsync(captured.History);
            Assert.That(answer.Text, Is.EqualTo("Sunny."));

            var replayRequest = handler.Requests[1];
            var contents = replayRequest.GetProperty("contents");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(handler.RequestUris[1].AbsoluteUri, Is.EqualTo("https://fixture.invalid/v1beta/models/gemini-3.5-flash:generateContent"));
                Assert.That(replayRequest.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString(), Is.EqualTo("Be concise."));
                Assert.That(contents[0].GetProperty("role").GetString(), Is.EqualTo("user"));
                Assert.That(contents[1].GetProperty("role").GetString(), Is.EqualTo("model"));
                Assert.That(contents[1].GetProperty("parts")[0].GetProperty("text").GetString(), Is.EqualTo("public-before"));
                Assert.That(contents[1].GetProperty("parts")[1].GetProperty("thought").GetBoolean(), Is.True);
                Assert.That(contents[1].GetProperty("parts")[1].GetProperty("thoughtSignature").GetString(), Is.EqualTo("private-thought"));
                Assert.That(contents[1].GetProperty("parts")[2].GetProperty("functionCall").GetProperty("name").GetString(), Is.EqualTo("weather"));
                Assert.That(contents[1].GetProperty("parts")[2].GetProperty("functionCall").GetProperty("args").GetProperty("city").GetString(), Is.EqualTo("Paris"));
                Assert.That(contents[1].GetProperty("parts")[2].GetProperty("thoughtSignature").GetString(), Is.EqualTo("call-sig"));
                Assert.That(contents[2].GetProperty("role").GetString(), Is.EqualTo("function"));
                Assert.That(contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("name").GetString(), Is.EqualTo("weather"));
                Assert.That(contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("response").GetProperty("content").GetString(), Is.EqualTo("sunny"));
                Assert.That(contents[3].GetProperty("role").GetString(), Is.EqualTo("model"));
                Assert.That(contents[3].GetProperty("parts")[0].GetProperty("text").GetString(), Is.EqualTo("continued"));
            }

            // Foreign provider tool turn must degrade to portable evidence instead of emitting unsigned native functionCall parts.
            using var foreignAssistant = new AssistantChatMessage();
            var foreignTool = new FunctionCallChatMessage(LucideIconKind.Hammer, null);
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
                GeminiGenerateContentClient.DialectName,
                supportsToolResultBlocks: false);

            await reader.GetResponseAsync(foreignInput.History);
            var degradedRaw = handler.Requests[2].GetProperty("contents").GetRawText();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(degradedRaw, Does.Not.Contain("functionCall").And.Not.Contain("functionResponse"));
                Assert.That(degradedRaw, Does.Contain("prior_tool_call").And.Contain("prior_tool_result").And.Contain("cloudy"));
            }
        }
        finally
        {
            assistant.Dispose();
        }
    }

    [Test]
    public async Task PreHarnessSignedToolFixture_WhenReplayedViaGenerateContent_PreservesCallThoughtSignature()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures/Harness/v2-signed-tool.msgpack"));
        using var assistant = (AssistantChatMessage)MessagePackSerializer.Deserialize<Everywhere.Chat.ChatMessage>(bytes);

        var captured = await ChatHistoryBuilder.CaptureModelInputAsync(
            Substitute.For<IPromptRenderer>(),
            string.Empty,
            [new UserChatMessage("question", []), assistant],
            Modalities.Text,
            null,
            CancellationToken.None,
            null,
            GeminiGenerateContentClient.DialectName,
            supportsToolResultBlocks: false);

        using var wire = new WireHandler();
        using var http = new HttpClient(wire);
        using var client = new GeminiGenerateContentClient(http, BaseUri, "gemini-2.5-pro", "synthetic-key");

        await client.GetResponseAsync(captured.History);

        var contents = wire.Requests.Single().GetProperty("contents");
        var modelTurn = contents[1];
        var callPart = modelTurn.GetProperty("parts").EnumerateArray().Single(static part => part.TryGetProperty("functionCall", out _));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(callPart.GetProperty("functionCall").GetProperty("name").GetString(), Is.EqualTo("lookup"));
            Assert.That(callPart.GetProperty("thoughtSignature").GetString(), Is.EqualTo("call-owned-signature"));
            Assert.That(contents[2].GetProperty("role").GetString(), Is.EqualTo("function"));
            Assert.That(contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("response").GetProperty("content").GetString(), Is.EqualTo("result-123"));
        }
    }

    [Test]
    public async Task Streaming_WhenConnectionEndsWithoutFinishReason_ThrowsEndOfStreamException()
    {
        var frames = new[]
        {
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"Checking."}]}}]}"""
        };
        using var http = new HttpClient(new WireHandler(frames));
        using var client = new GeminiGenerateContentClient(http, BaseUri, "fixture", "synthetic-key");

        var updates = new List<ChatResponseUpdate>();
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "weather?")]))
            {
                updates.Add(update);
            }
        });
        Assert.That(updates.SelectMany(static u => u.Contents).OfType<TextContent>().Single().Text, Is.EqualTo("Checking."));
    }

    private sealed class WireHandler(string[]? streamFrames = null) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri ?? throw new AssertionException("Missing request URI.");
            RequestUris.Add(uri);
            Assert.That(request.Headers.GetValues("x-goog-api-key").Single(), Is.EqualTo("synthetic-key"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Requests.Add(body.RootElement.Clone());
            var isStream = uri.AbsolutePath.EndsWith(":streamGenerateContent", StringComparison.Ordinal);
            var frames = streamFrames ??
            [
                """{"responseId":"resp-1","modelVersion":"gemini-2.5-pro","candidates":[{"content":{"role":"model","parts":[{"text":"public-before"}]}}]}""",
                """{"candidates":[{"content":{"role":"model","parts":[{"text":"thinking...","thought":true,"thoughtSignature":"private-thought"}]}}]}""",
                """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"id":"local-call","name":"weather","args":{"city":"Paris"}},"thoughtSignature":"call-sig"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":11,"candidatesTokenCount":8,"thoughtsTokenCount":10,"totalTokenCount":29}}"""
            ];
            var content = isStream
                ? string.Concat(frames.Select(static frame => $"data: {frame}\n\n"))
                : """{"responseId":"resp-2","modelVersion":"gemini-3.5-flash","candidates":[{"content":{"role":"model","parts":[{"text":"Sunny."}]},"finishReason":"STOP"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, isStream ? "text/event-stream" : "application/json")
            };
        }
    }
}
