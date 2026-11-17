using System.Net;
using System.Text;
using System.Text.Json;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using AnthropicModelSession = Everywhere.Harness.AnthropicModelSession;

namespace Everywhere.Core.Tests.Harness;

public sealed class ScopedToolProviderTests
{
    // A provider that recognizes only executable AIFunction objects silently omits the
    // entire catalog. Check the SDK's actual POST, not just ChatOptions in the host.
    [TestCase(ModelProviderSchema.OpenAI)]
    [TestCase(ModelProviderSchema.OpenAIResponses)]
    [TestCase(ModelProviderSchema.Anthropic)]
    [TestCase(ModelProviderSchema.Google)]
    [TestCase(ModelProviderSchema.GoogleInteractions)]
    [TestCase(ModelProviderSchema.Mistral)]
    [TestCase(ModelProviderSchema.Ollama)]
    public async Task StreamRequest_WhenToolHasScopedName_SendsItsDeclarationToTheProvider(ModelProviderSchema provider)
    {
        using var handler = new RequestRecorder();
        using var client = new HttpClient(handler);
        var configuration = new AdvancedAssistantConfiguration { Schema = provider, ModelId = "test-model" };
        var endpoint = provider.NormalizeEndpoint("https://fixture.invalid") ?? "https://fixture.invalid";
        var connection = new ModelConnection(provider, endpoint, "fixture-key", client, null, 0);
        using var session = CreateSession(configuration, connection);
        var schema = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""");
        var declaration = AIFunctionFactory.CreateDeclaration("lookup_file_1", "second server", schema);
        var request = session.StreamRequestAsync(
            [new ChatMessage(ChatRole.User, "look it up")],
            session.CreateChatOptions([declaration], ChatToolMode.Auto));
        // The fixture returns HTTP 400 after capturing the POST.
        await Assert.ThatAsync(async () => await request.ToListAsync(), Throws.TypeOf<ChatRequestException>());
        Assert.That(handler.Request, Is.Not.Null);
        var tool = handler.Request!.Value.GetProperty("tools").EnumerateArray().Single();
        if (provider == ModelProviderSchema.Google)
        {
            tool = tool.GetProperty("functionDeclarations").EnumerateArray().Single();
        }

        var function = tool.TryGetProperty("function", out var nested) ? nested : tool;
        Assert.That(function.GetProperty("name").GetString(), Is.EqualTo("lookup_file_1"));
        var input = function.GetProperty(provider == ModelProviderSchema.Anthropic ? "input_schema" : "parameters");
        Assert.That(input.GetProperty("properties").GetProperty("query").GetProperty("type").GetString(), Is.EqualTo("string"));
    }

    [TestCase(ModelProviderSchema.OpenAI)]
    [TestCase(ModelProviderSchema.OpenAIResponses)]
    [TestCase(ModelProviderSchema.Anthropic)]
    [TestCase(ModelProviderSchema.Google)]
    [TestCase(ModelProviderSchema.GoogleInteractions)]
    [TestCase(ModelProviderSchema.Mistral)]
    [TestCase(ModelProviderSchema.Ollama)]
    public async Task StreamRequest_WhenToolModeIsNoneWithTools_PreservesToolsArrayAcrossAllProviders(ModelProviderSchema provider)
    {
        using var handler = new RequestRecorder();
        using var client = new HttpClient(handler);
        var configuration = new AdvancedAssistantConfiguration { Schema = provider, ModelId = "test-model" };
        var endpoint = provider.NormalizeEndpoint("https://fixture.invalid") ?? "https://fixture.invalid";
        var connection = new ModelConnection(provider, endpoint, "fixture-key", client, null, 0);
        using var session = CreateSession(configuration, connection);
        var schema = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""");
        var declaration = AIFunctionFactory.CreateDeclaration("lookup_file_1", "second server", schema);
        var request = session.StreamRequestAsync(
            [new ChatMessage(ChatRole.User, "summarize")],
            session.CreateChatOptions([declaration], ChatToolMode.None));
        await Assert.ThatAsync(async () => await request.ToListAsync(), Throws.TypeOf<ChatRequestException>());
        Assert.That(handler.Request, Is.Not.Null);

        var body = handler.Request!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(body.TryGetProperty("tools", out var tools), Is.True);
            Assert.That(tools.GetArrayLength(), Is.GreaterThan(0));

            switch (provider)
            {
                case ModelProviderSchema.OpenAI:
                case ModelProviderSchema.OpenAIResponses:
                case ModelProviderSchema.Mistral:
                    Assert.That(body.GetProperty("tool_choice").GetString(), Is.EqualTo("none"));
                    break;
                case ModelProviderSchema.Anthropic:
                    Assert.That(body.GetProperty("tool_choice").GetProperty("type").GetString(), Is.EqualTo("none"));
                    break;
                case ModelProviderSchema.Google:
                    Assert.That(body.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString(), Is.EqualTo("NONE"));
                    break;
                case ModelProviderSchema.GoogleInteractions:
                    Assert.That(body.GetProperty("generation_config").GetProperty("tool_choice").GetString(), Is.EqualTo("none"));
                    break;
            }
        });
    }

    private static ModelSession CreateSession(AdvancedAssistantConfiguration configuration, ModelConnection connection)
    {
        return configuration.Schema switch
        {
            ModelProviderSchema.OpenAI => new OpenAIModelSession(configuration, new OpenAIOptions(), connection, NullLoggerFactory.Instance),
            ModelProviderSchema.OpenAIResponses => new OpenAIResponsesModelSession(configuration, new OpenAIResponsesOptions(), connection, NullLoggerFactory.Instance),
            ModelProviderSchema.Anthropic => new AnthropicModelSession(configuration, new AnthropicOptions(), connection),
            ModelProviderSchema.Google => new GoogleModelSession(configuration, new GoogleOptions(), connection),
            ModelProviderSchema.GoogleInteractions => new GoogleInteractionsModelSession(configuration, new GoogleInteractionsOptions(), connection),
            ModelProviderSchema.Mistral => new MistralModelSession(configuration, new MistralOptions(), connection),
            ModelProviderSchema.Ollama => new OllamaModelSession(configuration, connection),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
    }

    private sealed class RequestRecorder : HttpMessageHandler
    {
        public JsonElement? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"end of fixture"}}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
