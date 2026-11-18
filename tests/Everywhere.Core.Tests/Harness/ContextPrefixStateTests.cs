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
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Harness;
using Everywhere.I18N;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Skills;
using Everywhere.Statistics;
using Everywhere.Storage;
using Lucide.Avalonia;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Everywhere.Core.Tests.Harness;

public sealed class ContextPrefixStateTests
{
    [Test]
    public void Apply_WhenSystemPromptChangesWithinTtl_PreservesLockedSystemAndEmitsUnifiedDiff()
    {
        var state = new ContextPrefixState();
        var t0 = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var identity = new ContextPrefixIdentity("openai", "gpt-5", true, null, Guid.CreateVersion7());
        var tools = new[] { CreateTool("alpha"), CreateTool("beta") };

        static string BuildLongPrompt(string skills, string cwd, string date) =>
            $"""
             Unchanged header line 1: core persona and reasoning rules.
             Unchanged header line 2: verify facts before asserting conclusions.
             Unchanged header line 3: prefer concise, structured answers.
             Unchanged header line 4: respect explicit user constraints.
             Unchanged header line 5: follow repository conventions strictly.
             <Environment>
             Skills: {skills}
             WorkingDirectory: {cwd}
             Date: {date}
             </Environment>
             Unchanged footer line 1: format citations with markdown links.
             Unchanged footer line 2: avoid fabricating unverified tool results.
             Unchanged footer line 3: surface errors cleanly when blocked.
             Unchanged footer line 4: conclude with concrete next steps.
             """;

        var prompt1 = BuildLongPrompt("skill-a", "/work/a", "2026-11-17");
        var prompt2 = BuildLongPrompt("skill-a, skill-b", "/work/b", "2026-11-18");

        var first = state.Apply(
            [
                new ChatMessage(ChatRole.System, prompt1),
                new ChatMessage(ChatRole.User, "Turn 1")
            ],
            tools,
            identity,
            t0);

        Assert.That(first.HasResetEpoch, Is.True);
        Assert.That(first.History.Select(static m => m.Text), Is.EqualTo(new[] { prompt1, "Turn 1" }));

        var second = state.Apply(
            [
                new ChatMessage(ChatRole.System, prompt2),
                new ChatMessage(ChatRole.User, "Turn 1"),
                new ChatMessage(ChatRole.Assistant, "Reply 1"),
                new ChatMessage(ChatRole.User, "Turn 2")
            ],
            tools,
            identity,
            t0.AddMinutes(2));

        Assert.Multiple(() =>
        {
            Assert.That(second.HasResetEpoch, Is.False);
            Assert.That(second.History[0].Text, Is.EqualTo(prompt1));
            Assert.That(second.History[1].Text, Is.EqualTo("Turn 1"));
            Assert.That(second.History[2].Text, Is.EqualTo("Reply 1"));
            Assert.That(second.History[3].Role, Is.EqualTo(ChatRole.User));
            Assert.That(second.History[3].Text, Does.StartWith("<system-reminder>")
                .And.Contain("```diff")
                .And.Contain("-Skills: skill-a")
                .And.Contain("+Skills: skill-a, skill-b")
                .And.Contain("-WorkingDirectory: /work/a")
                .And.Contain("+WorkingDirectory: /work/b")
                .And.Contain("-Date: 2026-11-17")
                .And.Contain("+Date: 2026-11-18")
                .And.Not.Contain("Unchanged header line 1")
                .And.Not.Contain("Unchanged footer line 4"));
            Assert.That(second.History[4].Text, Is.EqualTo("Turn 2"));
        });

        var third = state.Apply(
            [
                new ChatMessage(ChatRole.System, prompt2),
                new ChatMessage(ChatRole.User, "Turn 1"),
                new ChatMessage(ChatRole.Assistant, "Reply 1"),
                new ChatMessage(ChatRole.User, "Turn 2"),
                new ChatMessage(ChatRole.Assistant, "Reply 2"),
                new ChatMessage(ChatRole.User, "Turn 3")
            ],
            tools,
            identity,
            t0.AddMinutes(4));

        Assert.Multiple(() =>
        {
            Assert.That(third.HasResetEpoch, Is.False);
            Assert.That(third.History, Has.Count.EqualTo(7));
            Assert.That(third.History[0].Text, Is.EqualTo(prompt1));
            Assert.That(third.History[3].Text, Is.EqualTo(second.History[3].Text));
            Assert.That(third.History[4].Text, Is.EqualTo("Turn 2"));
            Assert.That(third.History[5].Text, Is.EqualTo("Reply 2"));
            Assert.That(third.History[6].Text, Is.EqualTo("Turn 3"));
        });

        // Returning to the exact initial system prompt emits a concise restoration notice.
        var fourth = state.Apply(
            [
                new ChatMessage(ChatRole.System, prompt1),
                new ChatMessage(ChatRole.User, "Turn 1"),
                new ChatMessage(ChatRole.Assistant, "Reply 1"),
                new ChatMessage(ChatRole.User, "Turn 2"),
                new ChatMessage(ChatRole.Assistant, "Reply 2"),
                new ChatMessage(ChatRole.User, "Turn 3"),
                new ChatMessage(ChatRole.Assistant, "Reply 3"),
                new ChatMessage(ChatRole.User, "Turn 4")
            ],
            tools,
            identity,
            t0.AddMinutes(5));

        Assert.Multiple(() =>
        {
            Assert.That(fourth.HasResetEpoch, Is.False);
            Assert.That(fourth.History[8].Text, Does.Contain("Temporary instruction changes have ended"));
            Assert.That(fourth.History[9].Text, Is.EqualTo("Turn 4"));
        });
    }

    [Test]
    public void Apply_WhenToolsDisabledAndReenabled_PreservesWireToolsAndEmitsDeltaReminders()
    {
        var state = new ContextPrefixState();
        var t0 = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var identity = new ContextPrefixIdentity("anthropic", "claude-sonnet", true, null, Guid.CreateVersion7());
        const string systemPrompt = "System prompt";
        var alpha = CreateTool("alpha");
        var beta = CreateTool("beta");
        var gamma = CreateTool("gamma");

        state.Apply(
            [new ChatMessage(ChatRole.System, systemPrompt), new ChatMessage(ChatRole.User, "U1")],
            [alpha, beta, gamma],
            identity,
            t0);

        var disabled = state.Apply(
            [
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, "U1"),
                new ChatMessage(ChatRole.Assistant, "A1"),
                new ChatMessage(ChatRole.User, "U2")
            ],
            [alpha, gamma],
            identity,
            t0.AddMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(disabled.HasResetEpoch, Is.False);
            Assert.That(disabled.Tools?.Select(static t => t.Name), Is.EqualTo(new[] { "alpha", "beta", "gamma" }));
            Assert.That(disabled.History[3].Text, Does.Contain("no longer available").And.Contain("beta"));
        });

        var reenabled = state.Apply(
            [
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, "U1"),
                new ChatMessage(ChatRole.Assistant, "A1"),
                new ChatMessage(ChatRole.User, "U2"),
                new ChatMessage(ChatRole.Assistant, "A2"),
                new ChatMessage(ChatRole.User, "U3")
            ],
            [alpha, beta, gamma],
            identity,
            t0.AddMinutes(2));

        Assert.Multiple(() =>
        {
            Assert.That(reenabled.HasResetEpoch, Is.False);
            Assert.That(reenabled.Tools?.Select(static t => t.Name), Is.EqualTo(new[] { "alpha", "beta", "gamma" }));
            Assert.That(reenabled.History[6].Text, Does.Contain("available again").And.Contain("beta"));
            Assert.That(reenabled.History[7].Text, Is.EqualTo("U3"));
        });
    }

    [Test]
    public void Apply_WhenNewOrModifiedToolAppears_ResetsEpochWithUpdatedTools()
    {
        var state = new ContextPrefixState();
        var t0 = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var identity = new ContextPrefixIdentity("openai-responses", "gpt-5", true, null, Guid.CreateVersion7());

        state.Apply(
            [new ChatMessage(ChatRole.System, "Prompt V1"), new ChatMessage(ChatRole.User, "U1")],
            [CreateTool("alpha", "v1")],
            identity,
            t0);

        var addedTool = state.Apply(
            [
                new ChatMessage(ChatRole.System, "Prompt V2"),
                new ChatMessage(ChatRole.User, "U1"),
                new ChatMessage(ChatRole.Assistant, "A1"),
                new ChatMessage(ChatRole.User, "U2")
            ],
            [CreateTool("alpha", "v1"), CreateTool("beta")],
            identity,
            t0.AddMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(addedTool.HasResetEpoch, Is.True);
            Assert.That(addedTool.History[0].Text, Is.EqualTo("Prompt V2"));
            Assert.That(addedTool.History, Has.Count.EqualTo(4));
            Assert.That(addedTool.Tools?.Select(static t => t.Name), Is.EqualTo(new[] { "alpha", "beta" }));
        });

        var modifiedSchema = state.Apply(
            [
                new ChatMessage(ChatRole.System, "Prompt V2"),
                new ChatMessage(ChatRole.User, "U1"),
                new ChatMessage(ChatRole.Assistant, "A1"),
                new ChatMessage(ChatRole.User, "U2"),
                new ChatMessage(ChatRole.Assistant, "A2"),
                new ChatMessage(ChatRole.User, "U3")
            ],
            [CreateTool("alpha", "v2"), CreateTool("beta")],
            identity,
            t0.AddMinutes(2));

        Assert.That(modifiedSchema.HasResetEpoch, Is.True);
    }

    [Test]
    public void Apply_WhenBoundaryOrIdentityChangesOrTtlExpires_ResetsEpoch()
    {
        var state = new ContextPrefixState(TimeSpan.FromMinutes(5));
        var t0 = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var windowStart = Guid.CreateVersion7();
        var identity = new ContextPrefixIdentity("gemini-interactions", "gemini-2.5", true, null, windowStart);

        state.Apply([new ChatMessage(ChatRole.System, "P1"), new ChatMessage(ChatRole.User, "U1")], null, identity, t0);

        var expired = state.Apply(
            [new ChatMessage(ChatRole.System, "P2"), new ChatMessage(ChatRole.User, "U1"), new ChatMessage(ChatRole.User, "U2")],
            null,
            identity,
            t0.AddMinutes(6));
        Assert.Multiple(() =>
        {
            Assert.That(expired.HasResetEpoch, Is.True);
            Assert.That(expired.History[0].Text, Is.EqualTo("P2"));
        });

        var compressed = state.Apply(
            [new ChatMessage(ChatRole.System, "P1"), new ChatMessage(ChatRole.User, "Summary"), new ChatMessage(ChatRole.User, "U3")],
            null,
            identity with { ActiveSummaryNodeId = Guid.CreateVersion7() },
            t0.AddMinutes(7));
        Assert.That(compressed.HasResetEpoch, Is.True);

        var windowEvicted = state.Apply(
            [new ChatMessage(ChatRole.System, "P2"), new ChatMessage(ChatRole.User, "U2"), new ChatMessage(ChatRole.User, "U3")],
            null,
            identity with { ActiveSummaryNodeId = compressed.HasResetEpoch ? identity.ActiveSummaryNodeId : null, WindowStartNodeId = Guid.CreateVersion7() },
            t0.AddMinutes(8));
        Assert.That(windowEvicted.HasResetEpoch, Is.True);
    }

    [Test]
    public void Apply_WhenBranchRollsBack_PrunesAbandonedRemindersAndReemitsEffectiveDelta()
    {
        var state = new ContextPrefixState();
        var t0 = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var identity = new ContextPrefixIdentity("openai", "gpt-5", true, null, Guid.CreateVersion7());

        state.Apply([new ChatMessage(ChatRole.System, "Prompt S0"), new ChatMessage(ChatRole.User, "U1")], null, identity, t0);

        // Turn 2 adds reminder D1 (S0 -> S1) before U2 (AfterMessageCount = 2).
        state.Apply(
            [
                new ChatMessage(ChatRole.System, "Prompt S1"),
                new ChatMessage(ChatRole.User, "U1"),
                new ChatMessage(ChatRole.Assistant, "A1"),
                new ChatMessage(ChatRole.User, "U2")
            ],
            null,
            identity,
            t0.AddMinutes(1));

        // Tool loop step on the same branch adds reminder D2 (S1 -> S2) at tail (AfterMessageCount = 5).
        state.Apply(
            [
                new ChatMessage(ChatRole.System, "Prompt S2"),
                new ChatMessage(ChatRole.User, "U1"),
                new ChatMessage(ChatRole.Assistant, "A1"),
                new ChatMessage(ChatRole.User, "U2"),
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "alpha")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "ok")])
            ],
            null,
            identity,
            t0.AddMinutes(2));

        // User edits U2 -> U2'. Common prefix is [U1, A1] (length 2). D1 survives, D2 is pruned,
        // and because effective state rolls back to D1 (S1), S2 is cleanly re-emitted before U2'.
        var editedBranch = state.Apply(
            [
                new ChatMessage(ChatRole.System, "Prompt S2"),
                new ChatMessage(ChatRole.User, "U1"),
                new ChatMessage(ChatRole.Assistant, "A1"),
                new ChatMessage(ChatRole.User, "U2-edited")
            ],
            null,
            identity,
            t0.AddMinutes(3));

        Assert.Multiple(() =>
        {
            Assert.That(editedBranch.HasResetEpoch, Is.False);
            Assert.That(editedBranch.History[0].Text, Is.EqualTo("Prompt S0"));
            Assert.That(editedBranch.History.Select(static m => m.Text).ToArray(), Has.Exactly(1).Contains("S1"));
            Assert.That(editedBranch.History.Select(static m => m.Text).ToArray(), Has.Exactly(1).Contains("S2"));
            Assert.That(editedBranch.History[^1].Text, Is.EqualTo("U2-edited"));
        });
    }

    [Test]
    public void ProjectReadOnly_WhenEpochActive_ProjectsPrefixAndSurvivingRemindersWithoutMutatingSnapshot()
    {
        var state = new ContextPrefixState(TimeSpan.FromMinutes(10));
        var t0 = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var identity = new ContextPrefixIdentity("openai", "gpt-5", true, null, Guid.CreateVersion7());
        var alpha = CreateTool("alpha");
        var beta = CreateTool("beta");

        state.Apply(
            [new ChatMessage(ChatRole.System, "Prompt S0"), new ChatMessage(ChatRole.User, "U1")],
            [alpha, beta],
            identity,
            t0);

        state.Apply(
            [
                new ChatMessage(ChatRole.System, "Prompt S1"),
                new ChatMessage(ChatRole.User, "U1"),
                new ChatMessage(ChatRole.Assistant, "A1"),
                new ChatMessage(ChatRole.User, "U2")
            ],
            [alpha],
            identity,
            t0.AddMinutes(2));

        // Compression sees U1, A1, U2, plus newly finished A2 appended on this turn.
        var projected = state.ProjectReadOnly(
            [
                new ChatMessage(ChatRole.System, "Prompt S2"),
                new ChatMessage(ChatRole.User, "U1"),
                new ChatMessage(ChatRole.Assistant, "A1"),
                new ChatMessage(ChatRole.User, "U2"),
                new ChatMessage(ChatRole.Assistant, "A2")
            ],
            identity,
            t0.AddMinutes(3));

        Assert.Multiple(() =>
        {
            Assert.That(projected, Is.Not.Null);
            Assert.That(projected!.HasResetEpoch, Is.False);
            Assert.That(projected.Tools?.Select(static t => t.Name), Is.EqualTo(new[] { "alpha", "beta" }));
            Assert.That(projected.History[0].Text, Is.EqualTo("Prompt S0"));
            Assert.That(projected.History[3].Text, Does.Contain("beta").And.Contain("Prompt S1"));
            Assert.That(projected.History[4].Text, Is.EqualTo("U2"));
            Assert.That(projected.History[5].Text, Is.EqualTo("A2"));
        });

        // Verify ProjectReadOnly did NOT extend TTL from t0+2m to t0+3m or discard any state.
        var expiredByOriginalTtl = state.ProjectReadOnly(
            [
                new ChatMessage(ChatRole.System, "Prompt S1"),
                new ChatMessage(ChatRole.User, "U1"),
                new ChatMessage(ChatRole.Assistant, "A1"),
                new ChatMessage(ChatRole.User, "U2")
            ],
            identity,
            t0.AddMinutes(12).AddSeconds(1));
        Assert.That(expiredByOriginalTtl, Is.Null);

        // Head trimmed (commonPrefixLength == 0) returns null so compaction can drop locked tools.
        var trimmedHead = state.ProjectReadOnly(
            [
                new ChatMessage(ChatRole.System, "Prompt S1"),
                new ChatMessage(ChatRole.User, "U2"),
                new ChatMessage(ChatRole.Assistant, "A2")
            ],
            identity,
            t0.AddMinutes(3));
        Assert.That(trimmedHead, Is.Null);
    }

    [AvaloniaTest]
    public async Task ChatService_WhenToolsDisabledOrStrategyOverridesAcrossTurns_PreservesWirePrefixAndBlocksDisabledExecution()
    {
        using var db = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(db.Factory);
        var blobs = new BlobStorage(db.Factory);
        var services = Substitute.For<IServiceProvider>();
        var settings = new Settings(services);
        services.GetService(typeof(Settings)).Returns(settings);
        settings.Plugin.ApprovalMode = ToolApprovalMode.FullAccess;
        var probePlugin = new ProbePlugin();
        var plugins = new ChatPluginManager(
            services,
            [probePlugin],
            settings,
            Substitute.For<IRuntimeManager>(),
            NullLogger<ChatPluginManager>.Instance);
        using var releasePlugins = Disposable.Create(plugins.Dispose);
        await using var manager = new ChatContextManager(settings, storage, NullLogger<ChatContextManager>.Instance);

        var promptService = Substitute.For<IPromptService>();
        promptService.DefaultPrompt.Returns(
            new PromptDefinition(Guid.Empty, null, "Base system {SkillsPrompt} {WorkingDirectory}", IsBuiltIn: true));
        promptService.GetPromptAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<PromptDefinition?>(null));

        var currentSkills = "skill-alpha";
        var skillProvider = Substitute.For<ISkillPromptProvider>();
        skillProvider.GetPrompt(Arg.Any<ToolCallStatus>()).Returns(_ => currentSkills);

        using var wire = new WireRecorder();
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(wire, false));
        var factory = new ModelSessionFactory(http, NullLoggerFactory.Instance);
        var specializations = new AssistantSpecializationResolver(
            new AssistantCatalog(Substitute.For<IPresetModelProvider>(), Substitute.For<IOfficialModelProvider>()));
        using var visual = new ChatVisualService(new ImageViewRecoveryTests.UnusedHostConnectionSource());
        var chat = new ChatService(
            manager, plugins, factory, specializations, blobs, settings, promptService,
            skillProvider, Substitute.For<IStatisticsRecorder>(), visual,
            Substitute.For<IVisualContextScanEffect>(), services, NullLogger<ChatService>.Instance);

        var configuration = new AdvancedAssistantConfiguration
        {
            Schema = ModelProviderSchema.GoogleInteractions,
            Endpoint = "https://fixture.invalid/v1",
            ModelId = "gemini-test",
            SupportsToolCall = true,
            InputModalities = Modalities.Text
        };
        var assistant = new CustomAssistant { Configuration = configuration };

        var root = manager.Current;

        // Turn 1: Both tools enabled (declared in reverse registration order, sorted alphabetically), initial skills.
        var u1 = root.Data.StartTurn(new UserChatMessage("First turn", []));
        var a1 = root.Data.Append(u1, new AssistantChatMessage { IsBusy = true });
        await root.ExecuteAsync(token => chat.GenerateAsync(root, assistant, a1, CompressionBoundary.Before(u1), enableNotifications: false, cancellationToken: token));

        // Turn 2: Disable zebra_probe (must NOT disappear from wire tools), update skills & apply Strategy override.
        settings.Plugin.ToolEnablementRulesets[ToolSettingsKey.ForFunction(probePlugin.Key, "zebra_probe")] = false;
        currentSkills = "skill-beta";
        var u2 = root.Data.Append(a1, new UserChatMessage("Second turn", []));
        var a2 = root.Data.Append(u2, new AssistantChatMessage { IsBusy = true });
        await root.ExecuteAsync(token => chat.GenerateAsync(
            root,
            assistant,
            a2,
            CompressionBoundary.Before(u2),
            systemPromptOverride: "Temporary strategy instruction.",
            enableNotifications: false,
            cancellationToken: token));

        // Turn 3: Return to default system prompt -> emits restoration notice instead of resetting prefix.
        var u3 = root.Data.Append(a2, new UserChatMessage("Third turn", []));
        var a3 = root.Data.Append(u3, new AssistantChatMessage { IsBusy = true });
        await root.ExecuteAsync(token => chat.GenerateAsync(
            root,
            assistant,
            a3,
            CompressionBoundary.Before(u3),
            enableNotifications: false,
            cancellationToken: token));

        Assert.Multiple(() =>
        {
            Assert.That(wire.Requests, Has.Count.EqualTo(4));
            Assert.That(probePlugin.ZebraInvocationCount, Is.EqualTo(0));

            var tools0 = wire.Requests[0].GetProperty("tools").EnumerateArray().Select(static t => t.GetProperty("name").GetString()).ToArray();
            var tools1 = wire.Requests[1].GetProperty("tools").EnumerateArray().Select(static t => t.GetProperty("name").GetString()).ToArray();
            var tools3 = wire.Requests[3].GetProperty("tools").EnumerateArray().Select(static t => t.GetProperty("name").GetString()).ToArray();
            Assert.That(tools0, Is.EqualTo(new[] { "alpha_probe", "zebra_probe" }));
            Assert.That(tools1, Is.EqualTo(tools0));
            Assert.That(tools3, Is.EqualTo(tools0));

            var sys0 = wire.Requests[0].GetProperty("system_instruction").GetString();
            Assert.That(sys0, Does.Contain("skill-alpha"));
            Assert.That(wire.Requests[1].GetProperty("system_instruction").GetString(), Is.EqualTo(sys0));
            Assert.That(wire.Requests[2].GetProperty("system_instruction").GetString(), Is.EqualTo(sys0));
            Assert.That(wire.Requests[3].GetProperty("system_instruction").GetString(), Is.EqualTo(sys0));

            var turn2Input = wire.Requests[1].GetProperty("input").ToString();
            Assert.That(turn2Input, Does.Contain("zebra_probe").And.Contain("Temporary strategy instruction."));

            // Attempt 3 tried to call zebra_probe; ChatService rejected it without calling ZebraProbe.
            var turn2ToolResult = wire.Requests[2].GetProperty("input").ToString();
            Assert.That(turn2ToolResult, Does.Contain("zebra_probe").And.Contain("is not available"));

            var turn3Input = wire.Requests[3].GetProperty("input").ToString();
            Assert.That(turn3Input, Does.Contain("skill-beta"));
        });
    }

    private static AIFunctionDeclaration CreateTool(string name, string description = "Test tool") =>
        AIFunctionFactory.CreateDeclaration(
            name,
            description,
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"x":{"type":"string"}}}"""));

    private sealed class ProbePlugin : BuiltInChatPlugin
    {
        public override IDynamicLocaleKey HeaderKey => new DirectLocaleKey("Probe");
        public override IDynamicLocaleKey DescriptionKey => new DirectLocaleKey("Probe");
        public override LucideIconKind? Icon => LucideIconKind.Terminal;
        public override bool IsDefaultEnabled => true;

        public int ZebraInvocationCount { get; private set; }

        public ProbePlugin() : base("prefix_probe")
        {
            // Intentionally register zebra before alpha to verify deterministic sorting.
            _functionsSource.Add(new NativeChatFunction(ZebraProbe, ChatFunctionPermissions.None));
            _functionsSource.Add(new NativeChatFunction(AlphaProbe, ChatFunctionPermissions.None));
        }

        [ChatFunction("zebra_probe")]
        private string ZebraProbe()
        {
            ZebraInvocationCount++;
            return "zebra";
        }

        [ChatFunction("alpha_probe")]
        private string AlphaProbe() => "alpha";
    }

    private sealed class WireRecorder : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(doc.RootElement.Clone());
            var frames = Requests.Count switch
            {
                1 => new[]
                {
                    """{"event_type":"step.start","index":0,"step":{"type":"model_output","content":[{"type":"text","text":"First reply."}]}}""",
                    """{"event_type":"step.stop","index":0}""",
                    """{"event_type":"interaction.completed","interaction":{"id":"r1","status":"completed"}}"""
                },
                // Model stubbornly tries to call disabled tool zebra_probe on Turn 2!
                2 =>
                [
                    """{"event_type":"step.start","index":0,"step":{"type":"function_call","id":"call-z","name":"zebra_probe","arguments":{}}}""",
                    """{"event_type":"step.stop","index":0}""",
                    """{"event_type":"interaction.completed","interaction":{"id":"r2","status":"requires_action"}}"""
                ],
                3 =>
                [
                    """{"event_type":"step.start","index":0,"step":{"type":"model_output","content":[{"type":"text","text":"Second reply."}]}}""",
                    """{"event_type":"step.stop","index":0}""",
                    """{"event_type":"interaction.completed","interaction":{"id":"r3","status":"completed"}}"""
                ],
                _ =>
                [
                    """{"event_type":"step.start","index":0,"step":{"type":"model_output","content":[{"type":"text","text":"Third reply."}]}}""",
                    """{"event_type":"step.stop","index":0}""",
                    """{"event_type":"interaction.completed","interaction":{"id":"r4","status":"completed"}}"""
                ]
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Concat(frames.Select(static f => $"data: {f}\n\n")), Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
