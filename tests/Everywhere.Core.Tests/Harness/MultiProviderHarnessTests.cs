using System.Net;
using System.Text;
using System.Text.Json;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Harness;
using Everywhere.Harness.Providers.Mistral;
using MessagePack;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AnthropicModelSession = Everywhere.Harness.AnthropicModelSession;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace Everywhere.Core.Tests.Harness;

/// <summary>
/// Protects multi-provider MEAI continuation contracts (Anthropic thinking/redacted blocks,
/// OpenAI Responses encrypted reasoning items, Mistral native streaming, and native tool binding).
/// </summary>
public sealed class MultiProviderHarnessTests
{
    [Test]
    public async Task AnthropicSession_WhenStreamingThinkingEmptySignatureAndRedactedBlocks_RoundTripsExactWireBlocks()
    {
        const string sse =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-6\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":0}}}\n\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig-empty-46\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"redacted_thinking\",\"data\":\"redacted-payload\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"text\",\"text\":\"Answer.\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":2}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n";

        using var handler = new ScriptedHttpHandler(sse);
        using var session = new AnthropicModelSession(
            new AdvancedAssistantConfiguration { Schema = ModelProviderSchema.Anthropic, ModelId = "claude-sonnet-4-6" },
            new AnthropicOptions(),
            new ModelConnection(ModelProviderSchema.Anthropic, "https://fixture.invalid", "test-key", new HttpClient(handler, false), null, 0));

        using var assistant = new AssistantChatMessage();
        var assembler = BindAssembler(session.Dialect, assistant);
        await foreach (var update in session.ChatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hi")]))
        {
            assembler.AppendUpdate(update);
        }

        // Also inject a foreign unsigned reasoning span to verify Anthropic strips foreign reasoning while keeping native blocks.
        assistant.AddSpan(new AssistantChatMessageReasoningSpan("foreign plain reasoning")
        {
            Metadata = MetadataDictionary.FromDictionary(new Dictionary<string, object?> { [ReasoningMetadata.DialectKey] = "openai" })
        });

        var payload = MessagePackSerializer.Serialize<Everywhere.Chat.ChatMessage>(assistant);
        using var reopened = (AssistantChatMessage)MessagePackSerializer.Deserialize<Everywhere.Chat.ChatMessage>(payload);

        var captured = await ChatHistoryBuilder.CaptureModelInputAsync(
            Substitute.For<IPromptRenderer>(),
            "system",
            [new UserChatMessage("Hi", []), reopened, new UserChatMessage("Next", [])],
            Modalities.Text,
            null,
            CancellationToken.None,
            null,
            session.Dialect,
            session.SupportsToolResultBlocks);

        await session.ChatClient.GetStreamingResponseAsync(captured.History).ToListAsync();
        var secondRequest = handler.Requests[1];
        var assistantContent = secondRequest.GetProperty("messages")[1].GetProperty("content");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assistantContent.EnumerateArray().Select(static c => c.GetProperty("type").GetString()),
                Is.EqualTo(new[] { "thinking", "redacted_thinking", "text" }));
            Assert.That(assistantContent[0].GetProperty("thinking").GetString(), Is.EqualTo(string.Empty));
            Assert.That(assistantContent[0].GetProperty("signature").GetString(), Is.EqualTo("sig-empty-46"));
            Assert.That(assistantContent[1].GetProperty("data").GetString(), Is.EqualTo("redacted-payload"));
            Assert.That(secondRequest.GetRawText(), Does.Not.Contain("foreign plain reasoning"));
        }
    }

    [Test]
    public async Task OpenAIResponsesSession_WhenReasoningEncryptedContentReturned_RequestsIncludeAndReplaysEncryptedItem()
    {
        const string sse =
            "event: response.created\n" +
            "data: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"in_progress\",\"model\":\"o4-mini\",\"output\":[]}}\n\n" +
            "event: response.output_item.done\n" +
            "data: {\"type\":\"response.output_item.done\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"rs_abc\",\"type\":\"reasoning\",\"encrypted_content\":\"enc-blob-1\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"checked constraints\"}]}}\n\n" +
            "event: response.output_item.done\n" +
            "data: {\"type\":\"response.output_item.done\",\"sequence_number\":2,\"output_index\":1,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Done.\",\"annotations\":[]}]}}\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"sequence_number\":3,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"o4-mini\",\"output\":[]}}\n\n";

        using var handler = new ScriptedHttpHandler(sse);
        using var session = new OpenAIResponsesModelSession(
            new AdvancedAssistantConfiguration { Schema = ModelProviderSchema.OpenAIResponses, ModelId = "o4-mini" },
            new OpenAIResponsesOptions { ReasoningEffort = "medium" },
            new ModelConnection(ModelProviderSchema.OpenAIResponses, "https://fixture.invalid/v1", "test-key", new HttpClient(handler, false), null, 0),
            NullLoggerFactory.Instance);

        using var assistant = new AssistantChatMessage();
        var assembler = BindAssembler(session.Dialect, assistant);
        await foreach (var update in session.ChatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Solve")]))
        {
            assembler.AppendUpdate(update);
        }

        Assert.That(handler.Requests[0].GetProperty("include").EnumerateArray().Select(static x => x.GetString()),
            Does.Contain("reasoning.encrypted_content"));

        var payload = MessagePackSerializer.Serialize<Everywhere.Chat.ChatMessage>(assistant);
        using var reopened = (AssistantChatMessage)MessagePackSerializer.Deserialize<Everywhere.Chat.ChatMessage>(payload);

        var captured = await ChatHistoryBuilder.CaptureModelInputAsync(
            Substitute.For<IPromptRenderer>(),
            "system",
            [new UserChatMessage("Solve", []), reopened, new UserChatMessage("Follow up", [])],
            Modalities.Text,
            null,
            CancellationToken.None,
            null,
            session.Dialect,
            session.SupportsToolResultBlocks);

        await session.ChatClient.GetStreamingResponseAsync(captured.History).ToListAsync();
        var inputItems = handler.Requests[1].GetProperty("input");
        var reasoningItem = inputItems.EnumerateArray().Single(static i =>
            i.TryGetProperty("type", out var t) && t.GetString() == "reasoning");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reasoningItem.GetProperty("id").GetString(), Is.EqualTo("rs_abc"));
            Assert.That(reasoningItem.GetProperty("encrypted_content").GetString(), Is.EqualTo("enc-blob-1"));
        }
    }

    [Test]
    public async Task MistralChatClient_WhenStreamingThinkingAndToolCalls_AssemblesReasoningArgumentsAndUsage()
    {
        const string sse =
            "data: {\"id\":\"cmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"step 1\"}]}]}}]}\n\n" +
            "data: {\"id\":\"cmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_m1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\"}}]}}]}\n\n" +
            "data: {\"id\":\"cmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"a.txt\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":14,\"completion_tokens\":9,\"total_tokens\":23}}\n\n" +
            "data: [DONE]\n\n";

        using var handler = new ScriptedHttpHandler(sse);
        using var client = new MistralChatClient(
            new HttpClient(handler, false),
            new Uri("https://fixture.invalid/v1/chat/completions"),
            "magistral-medium",
            "mistral-key")
        {
            ReasoningEffort = "high"
        };

        var assembler = new ResponseAssembler(MistralChatClient.DialectName);
        var usage = new ChatUsageDetails();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Read a.txt")]))
        {
            assembler.AppendUpdate(update);
            usage.Update(update);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests[0].GetProperty("reasoning_effort").GetString(), Is.EqualTo("high"));
            Assert.That(assembler.Blocks.Single().IsReasoning, Is.True);
            Assert.That(assembler.Blocks.Single().Text?.ToString(), Is.EqualTo("step 1"));
            Assert.That(assembler.FunctionCalls, Has.Count.EqualTo(1));
            Assert.That(assembler.FunctionCalls[0].CallId, Is.EqualTo("call_m1"));
            Assert.That(assembler.FunctionCalls[0].Name, Is.EqualTo("read_file"));
            Assert.That(assembler.FunctionCalls[0].Arguments?["path"]?.ToString(), Is.EqualTo("a.txt"));
            Assert.That(usage.InputTokenCount, Is.EqualTo(14));
            Assert.That(usage.OutputTokenCount, Is.EqualTo(9));
            Assert.That(usage.TotalTokenCount, Is.EqualTo(23));
        }
    }

    [Test]
    public async Task NativeChatFunction_ExcludesServiceParametersFromSchemaAndNormalizesMissingRequiredArgument()
    {
        var probe = new ProbeTools();
        var fn = new NativeChatFunction(probe.Execute, ChatFunctionPermissions.None);
        var schema = fn.Function.JsonSchema;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fn.Function.Name, Is.EqualTo("probe_tool"));
            Assert.That(schema.GetProperty("properties").EnumerateObject().Select(static p => p.Name), Is.EqualTo(new[] { "query" }));
            Assert.That(fn.Function.ReturnJsonSchema, Is.Null);
        }

        var services = new ServiceCollection()
            .AddSingleton<ISampleInjectedService>(new SampleInjectedService("injected-ok"))
            .BuildServiceProvider();

        var ok = await fn.Function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "hello" }) { Services = services });
        Assert.That(ok, Is.EqualTo("injected-ok:hello"));

        var missingError = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await fn.Function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()) { Services = services }));
        var handled = HandledFunctionInvokingException.Handle(missingError!);
        Assert.That(handled, Is.TypeOf<HandledFunctionInvokingException>());
        Assert.That(((HandledFunctionInvokingException)handled).SubExceptionType, Is.EqualTo(HandledFunctionInvokingExceptionType.ArgumentMissing));
    }

    private static ResponseAssembler BindAssembler(string dialect, AssistantChatMessage assistant) =>
        new(
            dialect,
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

    private interface ISampleInjectedService
    {
        string Prefix { get; }
    }

    private sealed record SampleInjectedService(string Prefix) : ISampleInjectedService;

    private sealed class ProbeTools
    {
        [ChatFunction("probe_tool")]
        [Description("Probes service binding.")]
        public string Execute(
            [FromChatServices] ISampleInjectedService injected,
            [Description("Query text.")] string query) => $"{injected.Prefix}:{query}";
    }

    private sealed class ScriptedHttpHandler(string sseBody) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(doc.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
