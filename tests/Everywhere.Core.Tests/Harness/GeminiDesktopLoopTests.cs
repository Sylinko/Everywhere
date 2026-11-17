using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Disposables;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.NUnit;
using DynamicData;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Automation;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Core.Tests.Chat;
using Everywhere.Core.Tests.Configuration;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Harness;
using Everywhere.I18N;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Watchdog;
using Everywhere.Skills;
using Everywhere.Statistics;
using Everywhere.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using NSubstitute;
using AnthropicModelSession = Everywhere.Harness.AnthropicModelSession;

namespace Everywhere.Core.Tests.Harness;

[NonParallelizable]
public sealed class GeminiDesktopLoopTests
{
    [AvaloniaTest]
    public Task Generate_WhenReopened_ReplaysSignedToolTurnFromSpans()
    {
        return VerifyContinuationAsync("same");
    }

    [AvaloniaTest]
    public Task Generate_WhenKeyRotates_ReplaysSignedHistoryWithTheNewKey()
    {
        return VerifyContinuationAsync("key");
    }

    [AvaloniaTest]
    public Task Generate_WhenSwitchingGeminiModels_ReplaysSignedHistoryWithTheNewModel()
    {
        return VerifyContinuationAsync("model");
    }

    [AvaloniaTest]
    public Task Generate_WhenInstructionsChange_PreservesSignedSpanHistoryWithoutReExecutingTools()
    {
        return VerifyContinuationAsync("instructions");
    }

    [AvaloniaTest]
    public Task Generate_WhenNoKeyIsConfigured_ReplaysSignedHistoryAfterReload()
    {
        return VerifyContinuationAsync("anonymous");
    }

    private static async Task VerifyContinuationAsync(string nextContext)
    {
        using var vault = new SecretStoreScope();
        var slot = Guid.NewGuid();
        var secrets = new Dictionary<(string, string), string> { [(ApiKey.ServiceName, slot.ToString("N"))] = "synthetic-a" };
        vault.Store.GetSecret(Arg.Any<string>(), Arg.Any<string>()).Returns(c => secrets.GetValueOrDefault((c.ArgAt<string>(0), c.ArgAt<string>(1))));
        using var wire = new InteractionsServer();
        await WithDesktopAsync(wire, nextContext == "anonymous" ? Guid.Empty : slot, null, async (chat, assistant, manager, storage) =>
        {
            var root = manager.Current;
            var user = root.Data.Add(new UserChatMessage("Add this todo.", []));
            var node = root.Data.Append(user, new AssistantChatMessage { IsBusy = true });
            await root.ExecuteAsync(async token =>
            {
                var result = await chat.GenerateAsync(root, assistant, node, CompressionBoundary.Before(user),
                    systemPromptOverride: "Manage the todo.", enableNotifications: false, cancellationToken: token);
                Assert.That(result.Error, Is.Null, result.Error?.ToString());
                Assert.That(result.Output, Is.EqualTo("Added."));
            });
            Assert.That(wire.Requests, Has.Count.EqualTo(2));
            var tool = wire.Requests[0].GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "manage_todo");
            Assert.That(tool.GetProperty("parameters").GetProperty("properties").EnumerateObject().Select(p => p.Name),
                Is.EquivalentTo(new[] { "action", "items" }));
            Assert.That(root.UserInterfaceBroker.TodoItems.Single().Title, Is.EqualTo("test"));
            var steps = wire.Requests[1].GetProperty("input").EnumerateArray().ToArray();
            Assert.That(steps.Select(step => step.GetProperty("type").GetString()),
                Is.EqualTo(new[] { "user_input", "thought", "function_call", "function_result" }));
            Assert.That(steps[1].GetProperty("signature").GetString(), Is.EqualTo("opaque-test-signature"));
            Assert.That(steps[3].GetProperty("call_id").GetString(), Is.EqualTo("todo-1"));
            Assert.That(steps[3].GetProperty("result").ToString(), Does.Contain("Reset: 1 item."));

            await manager.FlushPendingChatAsync(root, CancellationToken.None);
            await using var reopened = await storage.LoadContextAsync(root.Data.Metadata.Id);
            foreach (var turn in reopened.Data.SelectedTurns) await reopened.EnsureTurnLoadedAsync(turn);
            var saved = (AssistantChatMessage)reopened.Data.FindNode(node.Id)!.Message;
            Assert.That(saved.Items.OfType<AssistantChatMessageFunctionCallSpan>().Single().Items.Single().Results, Has.Length.EqualTo(1));
            var savedReasoning = saved.Items.OfType<AssistantChatMessageReasoningSpan>().Single();
            Assert.That(savedReasoning.Metadata[ReasoningMetadata.ProtectedDataKey], Is.EqualTo("opaque-test-signature"));

            if (nextContext == "key")
            {
                secrets[(ApiKey.ServiceName, slot.ToString("N"))] = "synthetic-b";
            }

            if (nextContext == "model")
            {
                assistant.Configuration.ModelId = "gemini-next";
            }

            var nextUser = reopened.Data.Append(reopened.Data.FindNode(node.Id)!, new UserChatMessage("What was added?", []));
            var nextAssistant = reopened.Data.Append(nextUser, new AssistantChatMessage { IsBusy = true });
            await reopened.ExecuteAsync(async token =>
            {
                var result = await chat.GenerateAsync(reopened, assistant, nextAssistant, CompressionBoundary.Before(nextUser),
                    systemPromptOverride: nextContext == "instructions" ? "Review the todo." : "Manage the todo.",
                    enableNotifications: false, cancellationToken: token);
                Assert.That(result.Error, Is.Null, result.Error?.ToString());
            });
            Assert.That(wire.Requests, Has.Count.EqualTo(3));
            var continued = wire.Requests[2].GetProperty("input");
            Assert.That(continued.ToString(), Does.Contain("Reset: 1 item.").And.Contain("opaque-test-signature").And.Contain("function_result"));

            if (nextContext == "model")
            {
                Assert.That(wire.Requests[2].GetProperty("model").GetString(), Is.EqualTo("gemini-next"));
            }

            if (nextContext == "key")
            {
                Assert.That(wire.ApiKeys, Is.EqualTo(new[] { "synthetic-a", "synthetic-a", "synthetic-b" }));
            }
        });
    }

    private static async Task WithDesktopAsync(
        InteractionsServer wire,
        Guid key,
        BuiltInChatPlugin? extraPlugin,
        Func<ChatService, CustomAssistant, ChatContextManager, ChatTurnStorage, Task> verify,
        Action<Settings, ChatPluginManager, IServiceProvider>? configure = null)
    {
        using var db = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(db.Factory);
        var blobs = new BlobStorage(db.Factory);
        var services = Substitute.For<IServiceProvider>();
        var settings = new Settings(services);
        services.GetService(typeof(Settings)).Returns(settings);
        settings.Plugin.ApprovalMode = ToolApprovalMode.FullAccess;
        var specializations = new AssistantSpecializationResolver(
            new AssistantCatalog(Substitute.For<IPresetModelProvider>(), Substitute.For<IOfficialModelProvider>()));
        using var essential = new EssentialPlugin(settings, specializations, blobs);
        var plugins = new ChatPluginManager(
            services,
            extraPlugin is null ? [essential] : [essential, extraPlugin],
            settings,
            Substitute.For<IRuntimeManager>(),
            NullLogger<ChatPluginManager>.Instance);
        using var release = Disposable.Create(plugins.Dispose);
        configure?.Invoke(settings, plugins, services);
        await using var manager = new ChatContextManager(settings, storage, NullLogger<ChatContextManager>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(wire, false));
        var configuration = new AdvancedAssistantConfiguration
        {
            Schema = ModelProviderSchema.GoogleInteractions,
            Endpoint = "https://fixture.invalid/v1",
            ModelId = "gemini-test",
            ApiKey = key,
            SupportsToolCall = true,
            InputModalities = Modalities.Text
        };
        var assistant = new CustomAssistant { Configuration = configuration };
        var factory = new ModelSessionFactory(http, NullLoggerFactory.Instance);
        using var visual = new ChatVisualService(new ImageViewRecoveryTests.UnusedHostConnectionSource());
        var chat = new ChatService(
            manager, plugins, factory, specializations, blobs, settings, Substitute.For<IPromptService>(),
            Substitute.For<ISkillPromptProvider>(), Substitute.For<IStatisticsRecorder>(), visual,
            Substitute.For<IVisualContextScanEffect>(), services, NullLogger<ChatService>.Instance);

        await verify(chat, assistant, manager, storage);
    }

    [AvaloniaTest]
    public Task Generate_WhenMcpNamesCollide_InvokesOnlyTheApprovedServerWithItsOriginalName()
    {
        return VerifyMcpBindingAsync(true);
    }

    [AvaloniaTest]
    public Task Generate_WhenAliasedMcpCallIsDenied_ReturnsItsCallIdWithoutInvokingEitherServer()
    {
        return VerifyMcpBindingAsync(false);
    }

    [AvaloniaTest]
    public Task Generate_WhenRichToolTurnComesFromForeignProvider_ReplaysImagesAsPortableMediaWithoutRepeatingTheTool()
    {
        return VerifyMcpBindingAsync(true, simulateForeignOrigin: true);
    }

    private static async Task VerifyMcpBindingAsync(bool isAllowed, bool simulateForeignOrigin = false)
    {
        using var vault = new SecretStoreScope();
        var slot = Guid.NewGuid();
        var secrets = new Dictionary<(string, string), string> { [(ApiKey.ServiceName, slot.ToString("N"))] = "synthetic-mcp" };
        vault.Store.GetSecret(Arg.Any<string>(), Arg.Any<string>()).Returns(c => secrets.GetValueOrDefault((c.ArgAt<string>(0), c.ArgAt<string>(1))));
        using var rpc = new McpServers();
        var servers = new Dictionary<string, McpChatPlugin>();
        var approvalRules = default(ObservableToolRulesets);
        using var wire = new InteractionsServer([
            """{"event_type":"step.start","index":0,"step":{"type":"function_call","id":"lookup-2","name":"lookup_file_1","arguments":{"query":"rose"}}}""",
            """{"event_type":"step.stop","index":0}""",
            """{"event_type":"interaction.completed","interaction":{"id":"lookup","status":"requires_action"}}"""
        ]);
        await WithDesktopAsync(wire, slot, null, async (chat, assistant, manager, storage) =>
        {
            assistant.Configuration.InputModalities = Modalities.Text | Modalities.Image;
            var root = manager.Current;
            var prompts = 0;
            var pendingConsent = root.UserInterfaceBroker.ChatPluginUserInterfaceItems;
            pendingConsent.CollectionChanged += (_, _) =>
            {
                if (pendingConsent.OfType<ChatPluginUserInterfaceConsentRequestItem>().FirstOrDefault() is not { } consent)
                {
                    return;
                }

                prompts++;
                var decision = isAllowed
                    ? new ConsentDecision(ConsentDecisionKind.Custom, null, consent.CustomOptions?.Single())
                    : ConsentDecision.Deny();
                consent.Command.Execute(decision);
            };
            var user = root.Data.Add(new UserChatMessage("Look it up.", []));
            var node = root.Data.Append(user, new AssistantChatMessage { IsBusy = true });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await root.ExecuteAsync(async token =>
            {
                var result = await chat.GenerateAsync(
                    root,
                    assistant,
                    node,
                    CompressionBoundary.Before(user),
                    systemPromptOverride: "Look up a file.",
                    enableNotifications: false,
                    cancellationToken: token);
                Assert.That(result.Error, Is.Null, result.Error?.ToString());
            }, cancellationToken: timeout.Token);
            Assert.That(prompts, Is.EqualTo(1));
            var declarations = wire.Requests[0].GetProperty("tools")
                .EnumerateArray()
                .Where(tool => tool.GetProperty("name").GetString()?.StartsWith("lookup_file", StringComparison.Ordinal) is true)
                .ToArray();
            Assert.That(declarations.Select(t => t.GetProperty("description").GetString()), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(JsonElement.DeepEquals(declarations[1].GetProperty("parameters"), McpServers.Schema), Is.True);
            Assert.That(rpc.Calls, Is.EqualTo(isAllowed ? new[] { ("second", "lookup.file", "rose") } : []));
            Assert.That(approvalRules?.GetPluginRule(servers["first"]), Is.Null);
            Assert.That(approvalRules?.GetPluginRule(servers["second"]), Is.EqualTo(isAllowed ? true : (bool?)null));
            var reply = wire.Requests[1].GetProperty("input")
                .EnumerateArray()
                .Single(step => step.GetProperty("type").GetString() == "function_result");
            Assert.That(reply.GetProperty("call_id").GetString(), Is.EqualTo("lookup-2"));
            Assert.That(reply.GetProperty("result").ToString(), Does.Contain(isAllowed ? "found rose" : "denied"));
            if (!isAllowed) return;

            var blocks = reply.GetProperty("result");
            Assert.That(blocks.EnumerateArray().Select(block => block.GetProperty("type").GetString()),
                Is.EqualTo(new[] { "text", "image", "text", "text" }));
            Assert.That(blocks[1].GetProperty("data").GetString(), Is.EqualTo(McpServers.Image));
            Assert.That(blocks[3].GetProperty("text").GetString(), Does.Contain("score"));
            Assert.That(reply.GetRawText(), Does.Not.Contain("private-marker"));
            Assert.That(reply.GetProperty("is_error").GetBoolean(), Is.True);

            await manager.FlushPendingChatAsync(root, CancellationToken.None);
            await using var reopened = await storage.LoadContextAsync(root.Data.Metadata.Id);
            foreach (var turn in reopened.Data.SelectedTurns) await reopened.EnsureTurnLoadedAsync(turn);
            var savedNode = reopened.Data.FindNode(node.Id) ?? throw new AssertionException("Missing tool turn.");
            var saved = (AssistantChatMessage)savedNode.Message;
            var callSpan = saved.Spans.OfType<AssistantChatMessageFunctionCallSpan>().Single();
            var resultRecord = callSpan.Items.Single().Results.Single();
            var rich = (ToolContentResult)resultRecord.Result!;
            Assert.That(rich.ProtocolMetadata?.GetRawText(), Does.Contain("private-marker"));
            Assert.That(resultRecord.Files, Has.Length.EqualTo(1));
            Assert.That(File.Exists(resultRecord.Files[0].Attachment.FilePath), Is.True);

            if (simulateForeignOrigin)
            {
                // Simulate switching from Anthropic to Gemini on an existing rich MCP tool turn.
                callSpan.Metadata = callSpan.Metadata.SetItem(ReasoningMetadata.DialectKey, AnthropicModelSession.DialectName);
                var oldCall = callSpan.Items.Single().Calls.Single();
                var foreignGroup = new FunctionCallChatMessage(Lucide.Avalonia.LucideIconKind.Hammer, null);
                foreignGroup.AddCall(oldCall with
                {
                    Metadata = new Dictionary<string, object?> { [ReasoningMetadata.DialectKey] = AnthropicModelSession.DialectName }
                });
                foreignGroup.AddResult(resultRecord);
                saved.TruncateSpans(0);
                saved.AddSpan(new AssistantChatMessageFunctionCallSpan(foreignGroup)
                {
                    Metadata = MetadataDictionary.FromDictionary(new Dictionary<string, object?> { [ReasoningMetadata.DialectKey] = AnthropicModelSession.DialectName })
                });
            }

            var nextUser = reopened.Data.Append(savedNode, new UserChatMessage("Summarize.", []));
            var next = reopened.Data.Append(nextUser, new AssistantChatMessage { IsBusy = true });
            await reopened.ExecuteAsync(async token =>
            {
                var result = await chat.GenerateAsync(reopened, assistant, next, CompressionBoundary.Before(nextUser),
                    systemPromptOverride: "Look up a file.",
                    enableNotifications: false, cancellationToken: token);
                Assert.That(result.Error, Is.Null, result.Error?.ToString());
            });
            var input = wire.Requests[2].GetProperty("input");
            if (simulateForeignOrigin)
            {
                Assert.That(input.GetRawText(), Does.Not.Contain("function_result").And.Not.Contain("private-marker"));
                var evidence = input.EnumerateArray().Select(step => step.GetProperty("content"))
                    .Single(content => content[0].TryGetProperty("text", out var text) && text.GetString()!.Contains("prior_tool_result"));
                Assert.That(evidence[0].GetProperty("text").GetString(), Does.Not.Contain(McpServers.Image));
                Assert.That(evidence[2].GetProperty("type").GetString(), Is.EqualTo("image"));
                Assert.That(evidence[2].GetProperty("data").GetString(), Is.EqualTo(McpServers.Image));
            }
            else
            {
                var replayed = input.EnumerateArray().Single(step => step.GetProperty("type").GetString() == "function_result");
                Assert.That(JsonElement.DeepEquals(reply, replayed), Is.True);
            }

            Assert.That(rpc.Calls, Has.Count.EqualTo(1));
        }, (settings, plugins, services) =>
        {
            var http = Substitute.For<IHttpClientFactory>();
            http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(rpc, false));
            services.GetService(typeof(IHttpClientFactory)).Returns(http);
            services.GetService(typeof(IWatchdogManager)).Returns(Substitute.For<IWatchdogManager>());
            services.GetService(typeof(IKeyValueStorage)).Returns(Substitute.For<IKeyValueStorage>());
            services.GetService(typeof(IRuntimeManager)).Returns(Substitute.For<IRuntimeManager>());
            services.GetService(typeof(ILoggerFactory)).Returns(NullLoggerFactory.Instance);
            settings.Plugin.ApprovalMode = ToolApprovalMode.Ask;
            approvalRules = settings.Plugin.ToolBypassApprovalRulesets;
            foreach (var server in new[] { "first", "second" })
            {
                var plugin = plugins.CreateMcpPlugin(new HttpMcpTransportConfiguration
                {
                    Name = server,
                    Endpoint = $"https://{server}.invalid/mcp",
                    TransportMode = HttpTransportMode.StreamableHttp
                });
                servers.Add(server, plugin);
                settings.Plugin.ToolEnablementRulesets[ToolSettingsKey.ForPlugin(plugin)] = true;
            }
        });
    }

    private sealed class McpServers : HttpMessageHandler
    {
        public const string Image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=";

        public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{"query":{"$ref":"#/$defs/term"}},"required":["query"],"additionalProperties":false,"$defs":{"term":{"type":"string"}}}""");

        public List<(string Server, string Name, string Argument)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var message = doc.RootElement;
            if (!message.TryGetProperty("id", out var id)) return new HttpResponseMessage(HttpStatusCode.Accepted);
            var server = request.RequestUri!.Host.Split('.')[0];
            var method = message.GetProperty("method").GetString();
            var result = method switch
            {
                "server/discover" => new
                {
                    supportedVersions = new[] { "2026-07-28" },
                    capabilities = new { tools = new { } },
                    ttlMs = 0,
                    cacheScope = "private"
                },
                "tools/list" => new { tools = new[] { new { name = "lookup.file", description = server, inputSchema = Schema } } },
                "tools/call" => Call(server, message.GetProperty("params")),
                _ => throw new AssertionException($"Unexpected MCP method: {method}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), Encoding.UTF8, "application/json")
            };
        }

        private object Call(string server, JsonElement args)
        {
            var name = args.GetProperty("name").GetString() ?? throw new AssertionException("MCP tool name is missing.");
            var query = args.GetProperty("arguments").GetProperty("query").GetString() ?? throw new AssertionException("MCP query is missing.");
            Calls.Add((server, name, query));
            return new
            {
                content = new object[]
                {
                    new { type = "text", text = "found rose" },
                    new { type = "image", mimeType = "image/png", data = Image },
                    new { type = "text", text = "after image" }
                },
                isError = true,
                structuredContent = new { score = 42 },
                _meta = new { trace = "private-marker" }
            };
        }
    }

    [AvaloniaTest]
    public async Task Generate_WhenCallsInterleave_BatchesOnlyAdjacentOptInsAndReturnsResultsInNativeOrder()
    {
        using var plugin = new BatchPlugin();
        using var wire = new InteractionsServer(BatchFrames());
        await WithDesktopAsync(wire, Guid.Empty, plugin, async (chat, assistant, manager, _) =>
        {
            var root = manager.Current;
            var user = root.Data.Add(new UserChatMessage("Run tools.", []));
            var answer = new AssistantChatMessage { IsBusy = true };
            var node = root.Data.Append(user, answer);
            await root.ExecuteAsync(async token =>
            {
                var result = await chat.GenerateAsync(root, assistant, node, CompressionBoundary.Before(user),
                    systemPromptOverride: "Test tool order.", enableNotifications: false, cancellationToken: token);
                Assert.That(result.Error, Is.Null, result.Error?.ToString());
            });
            var ids = new[] { "read-1", "read-2", "barrier", "read-3" };
            var messages = answer.Items.OfType<AssistantChatMessageFunctionCallSpan>().Single().Items;
            Assert.That(messages.Select(row => row.Calls.Single().Id), Is.EqualTo(ids));
            Assert.That(messages.Select(row => row.Results.Single().CallId), Is.EqualTo(ids));
            Assert.That(plugin.Finished, Is.EqualTo(new[] { "read-2", "read-1", "barrier", "read-3" }));
            var results = wire.Requests[1].GetProperty("input").EnumerateArray().Where(s => s.GetProperty("type").GetString() == "function_result").ToArray();
            Assert.That(results.Select(r => r.GetProperty("call_id").GetString()), Is.EqualTo(ids));
            Assert.That(results[0].GetProperty("result").ToString(), Does.Contain("first failed"));
            Assert.That(results[3].GetProperty("result").ToString(), Does.Contain("read-3"));
        });
    }

    [AvaloniaTest]
    public async Task Generate_WhenParallelBatchIsCanceled_DrainsStartedCallsAndDoesNotStartSerialSuccessor()
    {
        using var plugin = new BatchPlugin(shouldWaitForCancellation: true);
        using var wire = new InteractionsServer(BatchFrames());
        await WithDesktopAsync(wire, Guid.Empty, plugin, async (chat, assistant, manager, _) =>
        {
            var root = manager.Current;
            var user = root.Data.Add(new UserChatMessage("Run tools.", []));
            var answer = new AssistantChatMessage { IsBusy = true };
            var node = root.Data.Append(user, answer);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = root.ExecuteAsync(async token =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
                await chat.GenerateAsync(root, assistant, node, CompressionBoundary.Before(user),
                    systemPromptOverride: "Test cancellation.", enableNotifications: false, cancellationToken: linked.Token);
            });
            try { await plugin.BothStarted.Task.WaitAsync(cancellation.Token); }
            finally { await cancellation.CancelAsync(); }
            await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(plugin.Finished, Is.EquivalentTo(new[] { "read-1", "read-2" }));
            Assert.That(wire.Requests, Has.Count.EqualTo(1));
            var rows = answer.Items.OfType<AssistantChatMessageFunctionCallSpan>().Single().Items;
            Assert.That(rows, Has.Count.EqualTo(4));
            Assert.That(rows.All(row => !row.IsBusy), Is.True);
            Assert.That(rows[2].Results, Is.Empty);
        });
    }

    private static string[] BatchFrames()
    {
        return
        [
            Start(0, "read-1", "read_probe"), Start(1, "read-2", "read_probe"),
            Stop(1), Stop(0),
            Start(2, "barrier", "serial_probe"), Stop(2), Start(3, "read-3", "read_probe"), Stop(3),
            """{"event_type":"interaction.completed","interaction":{"id":"batch","status":"requires_action"}}"""
        ];

        static string Start(int index, string id, string name) => JsonSerializer.Serialize(new
        {
            event_type = "step.start", index, step = new { type = "function_call", id, name, arguments = new { } }
        });
        static string Stop(int index) => JsonSerializer.Serialize(new { event_type = "step.stop", index });
    }

    private sealed class BatchPlugin : BuiltInChatPlugin
    {
        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Batch");
        public override IDynamicLocaleKey DescriptionKey => HeaderKey;
        public override bool IsDefaultEnabled => true;
        public ConcurrentQueue<string> Finished { get; } = new();
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _shouldWaitForCancellation;
        private int _started;
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BatchPlugin(bool shouldWaitForCancellation = false) : base("batch")
        {
            _shouldWaitForCancellation = shouldWaitForCancellation;
            _functionsSource.Add(new BuiltInChatFunction(ReadAsync, ChatFunctionPermissions.None) { SupportsParallelInvoke = true });
            _functionsSource.Add(new BuiltInChatFunction(Barrier, ChatFunctionPermissions.None));
        }

        [ChatFunction("read_probe")]
        private async Task<string> ReadAsync([FromChatServices] IChatPluginDisplaySink sink, CancellationToken cancellationToken)
        {
            var id = ((FunctionCallContext)sink).InvocationId;
            if (_shouldWaitForCancellation)
            {
                if (Interlocked.Increment(ref _started) == 2) BothStarted.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { Finished.Enqueue(id); }
            }

            if (id == "read-1")
            {
                _firstStarted.SetResult();
                await _secondFinished.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                Finished.Enqueue(id);
                throw new InvalidOperationException("first failed");
            }

            if (id == "read-2")
            {
                await _firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                Finished.Enqueue(id);
                _secondFinished.SetResult();
                return id;
            }

            Finished.Enqueue(id);
            return id;
        }

        [ChatFunction("serial_probe")]
        private string Barrier([FromChatServices] IChatPluginDisplaySink sink)
        {
            var id = ((FunctionCallContext)sink).InvocationId;
            Finished.Enqueue(id);
            return id;
        }
    }

    private sealed class InteractionsServer(string[]? firstFrames = null) : HttpMessageHandler
    {
        public List<string?> ApiKeys { get; } = [];
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.That(request.RequestUri?.AbsoluteUri, Is.EqualTo("https://fixture.invalid/v1/interactions"));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(document.RootElement.Clone());
            ApiKeys.Add(request.Headers.TryGetValues("x-goog-api-key", out var keys) ? keys.Single() : null);
            Assert.That(document.RootElement.GetProperty("store").GetBoolean(), Is.False);
            var frames = Requests.Count switch
            {
                1 => firstFrames ??
                [
                    """{"event_type":"step.start","index":0,"step":{"type":"thought","signature":"opaque-test-signature"}}""",
                    """{"event_type":"step.stop","index":0}""",
                    """{"event_type":"step.start","index":1,"step":{"type":"function_call","id":"todo-1","name":"manage_todo","arguments":{"action":"reset","items":[{"Id":1,"Title":"test"}]}}}""",
                    """{"event_type":"step.stop","index":1}""",
                    """{"event_type":"interaction.completed","interaction":{"id":"first","status":"requires_action"}}"""
                ],
                2 or 3 =>
                [
                    """{"event_type":"step.start","index":0,"step":{"type":"model_output","content":[{"type":"text","text":"Added."}]}}""",
                    """{"event_type":"step.stop","index":0}""",
                    """{"event_type":"interaction.completed","interaction":{"id":"last","status":"completed"}}"""
                ],
                _ => throw new AssertionException("A completed tool must not be dispatched again.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Concat(frames.Select(frame => $"data: {frame}\n\n")), Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
