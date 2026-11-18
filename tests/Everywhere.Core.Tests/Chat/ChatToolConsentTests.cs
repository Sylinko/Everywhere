using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Automation;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Harness;
using Everywhere.Harness.Mcp;
using Everywhere.I18N;
using Everywhere.Skills;
using Everywhere.Statistics;
using Everywhere.Storage;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using NSubstitute;
using LocaleKey = Everywhere.Core.I18N.LocaleKey;

namespace Everywhere.Core.Tests.Chat;

/// <summary>
/// Exercises outer approval and pre-call display hooks through <see cref="ChatService"/> with plugin snapshots.
/// </summary>
public sealed class ChatToolConsentTests
{
    /// <summary>
    /// MCP custom approval must work after the live plugin has become an ordinary chat snapshot.
    /// </summary>
    [AvaloniaTest]
    public async Task McpSnapshot_AllowToolGroup_RemembersStableKeyAndBypassesNextPrompt()
    {
        using var harness = new ConsentHarness(isMcp: true, ToolApprovalMode.Ask, consentHook: null);
        Assert.That(harness.Context.ChatPlugin, Is.Not.InstanceOf<McpChatPlugin>());

        var invocation = harness.InvokeAsync();
        var request = await harness.GetRequestAsync();
        Assert.That(
            request.HeaderKey,
            Is.EqualTo(
                new FormattedDynamicLocaleKey(
                    LocaleKey.ChatPluginConsentRequest_MCP_Header,
                    harness.Context.ChatFunction.HeaderKey)));
        var option = request.CustomOptions?.Single() ?? throw new AssertionException("Missing tool-group approval.");
        Assert.That(option.Key, Is.EqualTo(ToolConsentOption.BypassPluginApproval));
        request.Command.Execute(new ConsentDecision(ConsentDecisionKind.Custom, null, option));

        var firstResult = await invocation;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.Error?.InnerException, Is.TypeOf<NullReferenceException>());
            Assert.That(harness.Settings.Plugin.ToolBypassApprovalRulesets.GetPluginRule(harness.Context.ChatPlugin), Is.True);
            Assert.That(harness.Context.FunctionCallChatMessage.IsWaitingForUserInput, Is.False);
        }

        var secondResult = await harness.InvokeAsync();
        Assert.That(secondResult.Error?.InnerException, Is.TypeOf<NullReferenceException>());
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(harness.Context.ChatContext.UserInterfaceBroker.ChatPluginUserInterfaceItems, Is.Empty);
    }

    /// <summary>
    /// A null hook decision preserves host approval; explicit decisions and bypass rules avoid prompting.
    /// </summary>
    [AvaloniaTest]
    [TestCase(null, ToolApprovalMode.Ask, false, 1, 1)]
    [TestCase(true, ToolApprovalMode.Ask, false, 1, 1)]
    [TestCase(false, ToolApprovalMode.Ask, false, 1, 0)]
    [TestCase(null, ToolApprovalMode.Auto, false, 1, 1)]
    [TestCase(false, ToolApprovalMode.FullAccess, false, 0, 1)]
    [TestCase(false, ToolApprovalMode.Ask, true, 0, 1)]
    public async Task NativeSnapshot_ConsentHook_PreservesApprovalPipeline(
        bool? decision,
        ToolApprovalMode mode,
        bool hasRememberedApproval,
        int expectedHookCalls,
        int expectedExecutions)
    {
        var hookCalls = 0;
        FunctionCallContent? observedCall = null;
        using var harness = new ConsentHarness(
            isMcp: false,
            mode,
            (context, request) =>
            {
                hookCalls++;
                observedCall = context.FunctionCallContent;
                request.HeaderKey = new DirectLocaleKey("Custom request");
                request.RememberMasks = RequestConsentRememberMasks.AllowOnce;
                return decision;
            });
        if (hasRememberedApproval)
        {
            harness.Settings.Plugin.ToolBypassApprovalRulesets[ToolSettingsKey.ForPlugin(harness.Context.ChatPlugin)] = true;
        }

        var invocation = harness.InvokeAsync();
        if (decision is null && mode == ToolApprovalMode.Ask && !hasRememberedApproval)
        {
            var request = await harness.GetRequestAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(request.HeaderKey.ToString(), Is.EqualTo("Custom request"));
                Assert.That(request.RememberMasks, Is.EqualTo(RequestConsentRememberMasks.AllowOnce));
                Assert.That(harness.Context.FunctionCallChatMessage.IsWaitingForUserInput, Is.True);
            }
            request.Command.Execute(ConsentDecision.AllowOnce);
        }

        await invocation;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hookCalls, Is.EqualTo(expectedHookCalls));
            if (hookCalls > 0) Assert.That(observedCall, Is.SameAs(harness.Context.FunctionCallContent));
            Assert.That(harness.ExecutionCount, Is.EqualTo(expectedExecutions));
            Assert.That(harness.ReviewCount, Is.EqualTo(mode == ToolApprovalMode.Auto ? 1 : 0));
            Assert.That(harness.Context.FunctionCallChatMessage.IsWaitingForUserInput, Is.False);
        }
    }

    /// <summary>
    /// Verifies that the display block created by <see cref="BeforeCallHook"/> is appended as a fallback when outer approval denies.
    /// </summary>
    [AvaloniaTest]
    public async Task NativeSnapshot_BeforeCallHook_ProvidesFallbackDisplayBlockOnDeny()
    {
        var expectedBlock = new ChatPluginTextDisplayBlock("Preview payload");
        using var harness = new ConsentHarness(
            isMcp: false,
            ToolApprovalMode.Ask,
            consentHook: static (_, _) => false,
            beforeCallHook: _ => expectedBlock);

        var displayBlock = harness.Context.ChatFunction.BeforeCallHook?.Invoke(harness.Context.FunctionCallContent);
        var result = await harness.InvokeAsync(displayBlock);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(harness.ExecutionCount, Is.Zero);
            Assert.That(harness.Context.FunctionCallChatMessage.DisplaySink.Items, Has.Count.EqualTo(1));
            Assert.That(harness.Context.FunctionCallChatMessage.DisplaySink.Items[0], Is.SameAs(expectedBlock));
        }
    }

    private sealed class ConsentHarness : IDisposable
    {
        public Settings Settings { get; } = new(Substitute.For<IServiceProvider>());
        public FunctionCallContext Context { get; }
        public int ExecutionCount { get; private set; }
        public int ReviewCount { get; private set; }

        private readonly ChatPlugin _plugin;
        private readonly ChatData _chatData;
        private readonly RootChatContext _rootChatContext;
        private readonly ModelSession _session;
        private readonly GenerationContext _generation;
        private readonly ChatService _service;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));

        public ConsentHarness(
            bool isMcp,
            ToolApprovalMode mode,
            PermissionConsentHook? consentHook,
            BeforeCallHook? beforeCallHook = null)
        {
            _plugin = isMcp ?
                new McpChatPlugin(new StdioMcpTransportConfiguration { Name = "Remote", Command = "npx" }) :
                new TestPlugin();
            ChatFunction function = isMcp ?
                CreateMcpFunction() :
                new NativeChatFunction(
                    () =>
                    {
                        ExecutionCount++;
                        return "approved";
                    },
                    ChatFunctionPermissions.AllAccess,
                    beforeCallHook: beforeCallHook,
                    permissionConsentHook: consentHook,
                    functionName: "consent_probe");

            var snapshotType = typeof(ChatPluginManager).GetNestedType("ChatPluginSnapshot", BindingFlags.NonPublic) ??
                throw new AssertionException("Missing production snapshot.");
            var snapshot = (ChatPlugin)(Activator.CreateInstance(
                    snapshotType,
                    _plugin,
                    new NumberedDeduplicator(),
                    new NumberedDeduplicator(),
                    new[] { function }) ??
                throw new AssertionException("Missing snapshot constructor."));
            var binding = new ToolBinding(snapshot, function, new NumberedDeduplicator());

            _chatData = new ChatData();
            _rootChatContext = new RootChatContext(_chatData, null);
            _session = Substitute.For<ModelSession>(
                new AdvancedAssistantConfiguration(),
                new ModelConnection(ModelProviderSchema.OpenAI, "https://example.invalid", null, new HttpClient(), null));
            _session.Dialect.Returns("openai");
            _session.ChatClient.Returns(Substitute.For<IChatClient>());

            _generation = new GenerationContext(
                new ServiceCollection().BuildServiceProvider(),
                _session,
                null,
                Substitute.For<IPromptRenderer>(),
                "constraints",
                Modalities.Text,
                80,
                -1,
                new GenerationApprovalState(mode),
                isConversationTurn: true,
                _chatData.SelectedNodes[^1]);

            var call = new FunctionCallContent(Guid.NewGuid().ToString(), binding.Declaration.Name);
            Context = new FunctionCallContext(
                _generation.Services,
                _rootChatContext,
                binding,
                new FunctionCallChatMessage(LucideIconKind.File, new DirectLocaleKey("test")),
                call,
                Settings.Plugin.ToolBypassApprovalRulesets,
                _generation,
                (_, _, _) =>
                {
                    ReviewCount++;
                    return Task.FromResult(ToolApprovalResult.Allow("Reviewed"));
                });

            _service = new ChatService(
                Substitute.For<IChatContextManager>(),
                Substitute.For<IChatPluginManager>(),
                Substitute.For<IModelSessionFactory>(),
                null!,
                Substitute.For<IBlobStorage>(),
                Settings,
                Substitute.For<IPromptService>(),
                Substitute.For<ISkillPromptProvider>(),
                Substitute.For<IStatisticsRecorder>(),
                null!,
                Substitute.For<IVisualContextScanEffect>(),
                Substitute.For<IServiceProvider>(),
                Substitute.For<ILogger<ChatService>>());
        }

        private static McpChatFunction CreateMcpFunction()
        {
            var tool = (ManagedMcpClientTool)(Activator.CreateInstance(
                    typeof(ManagedMcpClientTool),
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    [new Tool { Name = "search", InputSchema = JsonElement.Parse("""{"type":"object","properties":{}}""") }, null, "search"],
                    null) ??
                throw new AssertionException("Missing managed tool constructor."));
            return new McpChatFunction(tool);
        }

        public Task<(ToolResultRecord Record, Exception? Error)> InvokeAsync(ChatPluginDisplayBlock? displayBlock = null)
        {
            var method = typeof(ChatService).GetMethod("InvokeFunctionAsync", BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new AssertionException("Missing invocation method.");
            return (Task<(ToolResultRecord Record, Exception? Error)>)(method.Invoke(
                    _service,
                    [_session, Context.FunctionCallContent, Context, displayBlock, _timeout.Token]) ??
                throw new AssertionException("Missing invocation task."));
        }

        public async Task<ChatPluginUserInterfaceConsentRequestItem> GetRequestAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            return Context.ChatContext.UserInterfaceBroker.ChatPluginUserInterfaceItems
                .OfType<ChatPluginUserInterfaceConsentRequestItem>()
                .Single();
        }

        public void Dispose()
        {
            _timeout.Cancel();
            Context.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _generation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _rootChatContext.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _chatData.Dispose();
            Context.ChatPlugin.Dispose();
            _plugin.Dispose();
            _session.Dispose();
            _timeout.Dispose();
        }
    }

    private sealed class TestPlugin() : BuiltInChatPlugin("consent")
    {
        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Consent tool");
        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Consent test");
    }
}
