using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless.NUnit;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Harness;
using Everywhere.Harness.Mcp;
using Everywhere.I18N;
using Everywhere.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using AnthropicModelSession = Everywhere.Harness.AnthropicModelSession;

namespace Everywhere.Core.Tests.Harness;

public sealed partial class ScopedToolProviderTests
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
                    Assert.That(
                        body.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString(),
                        Is.EqualTo("NONE"));
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
            ModelProviderSchema.OpenAIResponses => new OpenAIResponsesModelSession(
                configuration,
                new OpenAIResponsesOptions(),
                connection,
                NullLoggerFactory.Instance),
            ModelProviderSchema.Anthropic => new AnthropicModelSession(configuration, new AnthropicOptions(), connection),
            ModelProviderSchema.Google => new GoogleModelSession(configuration, new GoogleOptions(), connection),
            ModelProviderSchema.GoogleInteractions => new GoogleInteractionsModelSession(configuration, new GoogleInteractionsOptions(), connection),
            ModelProviderSchema.Mistral => new MistralModelSession(configuration, new MistralOptions(), connection),
            ModelProviderSchema.Ollama => new OllamaModelSession(configuration, connection),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
    }

    [AvaloniaTest]
    public void ToolBinding_FormatsMcpNamespacesSanitizesCharactersAndClampsTo64Chars()
    {
        var escapeMethod = typeof(ManagedMcpClient).GetMethod("EscapeToolName", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertionException("Missing ManagedMcpClient.EscapeToolName.");
        var escaped = (string)(escapeMethod.Invoke(null, ["fs/read-tool.v1 中文"])
            ?? throw new AssertionException("EscapeToolName returned null."));

        using var builtIn = new SampleBuiltInPlugin();
        using var firstMcp = new McpChatPlugin(new StdioMcpTransportConfiguration { Name = "first", Command = "npx" });
        using var secondMcp = new McpChatPlugin(new StdioMcpTransportConfiguration { Name = "second", Command = "npx" });
        var pluginDeduplicator = new NumberedDeduplicator();
        var deduplicator = new NumberedDeduplicator();

        var builtInSnapshot = builtIn.CreateSnapshot(
            pluginDeduplicator,
            deduplicator,
            [new NativeChatFunction((Func<string>)(static () => "ok"), ChatFunctionPermissions.None, functionName: "search_files")]);
        var builtInBinding = builtInSnapshot.Bindings[0];

        var longRawName = "shared_" + new string('a', 60);
        var mcpFunction1 = new McpChatFunction(CreateManagedMcpTool(longRawName));
        var mcpFunction2 = new McpChatFunction(CreateManagedMcpTool(longRawName));
        var dirtyMcpFunction = new McpChatFunction(CreateManagedMcpTool("dir/open:item 你好"));

        var mcpSnapshot1 = firstMcp.CreateSnapshot(pluginDeduplicator, deduplicator, [mcpFunction1, dirtyMcpFunction]);
        var mcpSnapshot2 = secondMcp.CreateSnapshot(pluginDeduplicator, deduplicator, [mcpFunction2]);
        var mcpBinding1 = mcpSnapshot1.Bindings[0];
        var dirtyBinding = mcpSnapshot1.Bindings[1];
        var mcpBinding2 = mcpSnapshot2.Bindings[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(escaped, Is.EqualTo("fs_read_tool_v1___"));
            Assert.That(escapeMethod.Invoke(null, [""]), Is.EqualTo("tool"));
            Assert.That(builtInBinding.Plugin, Is.TypeOf<BuiltInChatPluginSnapshot>());
            Assert.That(mcpBinding1.Plugin, Is.TypeOf<McpChatPluginSnapshot>());
            Assert.That(builtInBinding.Declaration.Name, Is.EqualTo("search_files"));
            Assert.That(mcpBinding1.Declaration.Name, Does.StartWith("mcp__shared_"));
            Assert.That(mcpBinding1.Declaration.Name, Has.Length.EqualTo(ToolBinding.MaxWireNameLength));
            Assert.That(mcpBinding2.Declaration.Name, Does.StartWith("mcp__shared_").And.EndWith("_1"));
            Assert.That(mcpBinding2.Declaration.Name, Has.Length.EqualTo(ToolBinding.MaxWireNameLength));
            Assert.That(mcpBinding2.Declaration.Name, Is.Not.EqualTo(mcpBinding1.Declaration.Name));
            Assert.That(dirtyBinding.Declaration.Name, Is.EqualTo("mcp__dir_open_item___"));
            Assert.That(WireNameRegex().IsMatch(mcpBinding1.Declaration.Name), Is.True);
            Assert.That(WireNameRegex().IsMatch(mcpBinding2.Declaration.Name), Is.True);
            Assert.That(WireNameRegex().IsMatch(dirtyBinding.Declaration.Name), Is.True);
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex WireNameRegex();

    [Test]
    public void NativeChatFunction_WhenNameViolatesWirePatternOrMaxLength_ThrowsArgumentException()
    {
        foreach (var invalidName in new[]
                     { "", "bad.name", "bad/name", "bad name", "工具", new string('a', NativeChatFunction.MaxNativeFunctionNameLength + 1) })
        {
            Assert.Throws<ArgumentException>(() => _ = new NativeChatFunction(
                (Func<string>)(static () => "ok"),
                ChatFunctionPermissions.None,
                functionName: invalidName));

            var rawFunction = AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions { Name = invalidName });
            Assert.Throws<ArgumentException>(() => _ = new NativeChatFunction(rawFunction, ChatFunctionPermissions.None));
        }

        var maxValidName = "valid-tool_" + new string('a', NativeChatFunction.MaxNativeFunctionNameLength - "valid-tool_".Length);
        var validFunction = new NativeChatFunction(
            (Func<string>)(static () => "ok"),
            ChatFunctionPermissions.None,
            functionName: maxValidName);
        Assert.That(validFunction.Name, Is.EqualTo(maxValidName));
    }

    private static ManagedMcpClientTool CreateManagedMcpTool(string name)
    {
        return (ManagedMcpClientTool)(Activator.CreateInstance(
                typeof(ManagedMcpClientTool),
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [new Tool { Name = name, InputSchema = JsonElement.Parse("""{"type":"object","properties":{}}""") }, null, name],
                null)
            ?? throw new AssertionException("Missing ManagedMcpClientTool constructor."));
    }

    private sealed class SampleBuiltInPlugin : BuiltInChatPlugin
    {
        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Built-in");
        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Built-in test plugin");

        public SampleBuiltInPlugin() : base("sample_builtin") { }
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
