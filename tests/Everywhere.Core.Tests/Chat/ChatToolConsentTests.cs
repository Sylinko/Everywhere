using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.Mcp;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Statistics;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Protocol;
using NSubstitute;
using LocaleKey = Everywhere.Core.I18N.LocaleKey;

namespace Everywhere.Core.Tests.Chat;

/// <summary>
/// Exercises outer approval through ChatService with the same snapshots used during chat execution.
/// </summary>
public sealed class ChatToolConsentTests
{
    /// <summary>
    /// MCP custom approval must work after the live plugin has become an ordinary chat snapshot.
    /// </summary>
    [AvaloniaTest]
    public async Task McpSnapshot_AllowToolGroup_RemembersStableKeyAndBypassesNextPrompt()
    {
        using var harness = new ConsentHarness(true, ToolApprovalMode.Ask, null);
        Assert.That(harness.Context.ChatPlugin, Is.Not.InstanceOf<McpChatPlugin>());
        var invocation = harness.InvokeAsync();
        var request = await harness.GetRequestAsync();
        Assert.That(request.HeaderKey, Is.EqualTo(new FormattedDynamicLocaleKey(
            LocaleKey.ChatPluginConsentRequest_MCP_Header, harness.Context.ChatFunction.HeaderKey)));
        var option = request.CustomOptions?.Single() ?? throw new AssertionException("Missing tool-group approval.");
        Assert.That(option.Key, Is.EqualTo(ToolConsentOption.BypassPluginApproval));
        request.Command.Execute(new ConsentDecision(ConsentDecisionKind.Custom, null, option));
        await invocation;
        Assert.That(harness.Settings.Plugin.ToolBypassApprovalRulesets.GetPluginRule(harness.Context.ChatPlugin), Is.True);
        Assert.That(harness.Context.FunctionCallChatMessage.IsWaitingForUserInput, Is.False);

        await harness.InvokeAsync();
        Assert.That(harness.ExecutionCount, Is.EqualTo(2));
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
        bool? decision, ToolApprovalMode mode, bool hasRememberedApproval, int expectedHookCalls, int expectedExecutions)
    {
        var hookCalls = 0;
        FunctionCallContent? observedCall = null;
        using var harness = new ConsentHarness(false, mode, (context, request) =>
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
        if (decision is null && mode == ToolApprovalMode.Ask)
        {
            var request = await harness.GetRequestAsync();
            Assert.That(request.HeaderKey.ToString(), Is.EqualTo("Custom request"));
            Assert.That(request.RememberMasks, Is.EqualTo(RequestConsentRememberMasks.AllowOnce));
            Assert.That(harness.Context.FunctionCallChatMessage.IsWaitingForUserInput, Is.True);
            request.Command.Execute(ConsentDecision.AllowOnce);
        }

        await invocation;
        Assert.That(hookCalls, Is.EqualTo(expectedHookCalls));
        if (hookCalls > 0) Assert.That(observedCall, Is.SameAs(harness.Context.FunctionCallContent));
        Assert.That(harness.ExecutionCount, Is.EqualTo(expectedExecutions));
        Assert.That(harness.ReviewCount, Is.EqualTo(mode == ToolApprovalMode.Auto ? 1 : 0));
        Assert.That(harness.Context.FunctionCallChatMessage.IsWaitingForUserInput, Is.False);
    }

    /// <summary>
    /// Uses real snapshot construction and ChatService approval; an SK filter prevents external tool work.
    /// </summary>
    private sealed class ConsentHarness : IDisposable, IFunctionInvocationFilter
    {
        /// <summary>
        /// Gets the invocation and its persistent snapshot identity.
        /// </summary>
        public FunctionCallContext Context { get; }

        /// <summary>
        /// Gets the settings written by production approval handling.
        /// </summary>
        public Settings Settings { get; } = new(Substitute.For<IServiceProvider>());

        /// <summary>
        /// Gets the number of calls that passed approval and entered SK execution.
        /// </summary>
        public int ExecutionCount { get; private set; }

        /// <summary>
        /// Gets the number of automatic reviews requested by the host.
        /// </summary>
        public int ReviewCount { get; private set; }

        private readonly ChatService _service;
        private readonly ChatPlugin _plugin;
        private readonly KernelMixin _mixin;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));

        /// <summary>
        /// Creates a live tool and then the production snapshot without connecting to an MCP server.
        /// </summary>
        public ConsentHarness(bool isMcp, ToolApprovalMode mode, PermissionConsentHook? hook)
        {
            var function = isMcp ?
                CreateMcpFunction() :
                new NativeChatFunction(() => "value", ChatFunctionPermissions.FileRead,
                    isDefaultBypassApproval: false, permissionConsentHook: hook);
            _plugin = isMcp ?
                new McpChatPlugin(new StdioMcpTransportConfiguration { Name = "Search server", Command = "unused" }) :
                new TestPlugin();

            var snapshotType = typeof(ChatPluginManager).GetNestedType("ChatPluginSnapshot", BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing production snapshot.");
            var snapshot = (ChatPlugin)(Activator.CreateInstance(snapshotType,
                [_plugin, new NumberedDeduplicator(), new NumberedDeduplicator(), new[] { function }])
                ?? throw new AssertionException("Missing snapshot constructor."));
            var kernel = new Kernel();
            kernel.Plugins.Add(snapshot);
            kernel.FunctionInvocationFilters.Add(this);
            _mixin = Substitute.For<KernelMixin>(new AdvancedAssistantConfiguration(),
                new ModelConnection(ModelProviderSchema.OpenAI, "https://example.invalid", null, new HttpClient(), null));
            var generation = new GenerationContext(kernel, _mixin, Substitute.For<IPromptRenderer>(), "constraints",
                Modalities.Text, 80, -1, new GenerationApprovalState(mode), true);
            var call = new FunctionCallContent(snapshot.Single().Name, id: Guid.NewGuid().ToString());
            Context = new FunctionCallContext(kernel, new ChatContext(), snapshot, function,
                new FunctionCallChatMessage(LucideIconKind.File, new DirectLocaleKey("test")), call,
                Settings.Plugin.ToolBypassApprovalRulesets, generation, (_, _, _) =>
                {
                    ReviewCount++;
                    return Task.FromResult(ToolApprovalResult.Allow("Reviewed"));
                });
            var constructor = typeof(ChatService).GetConstructors().Single();
            _service = (ChatService)constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.ParameterType switch
            {
                var type when type == typeof(Settings) => (object)Settings,
                var type when type == typeof(IStatisticsRecorder) => Substitute.For<IStatisticsRecorder>(),
                var type when type == typeof(ILogger<ChatService>) => Substitute.For<ILogger<ChatService>>(),
                _ => null
            }).ToArray());
        }

        /// <summary>
        /// Creates an MCP function whose client is never used because the invocation filter handles execution.
        /// </summary>
        private static ChatFunction CreateMcpFunction()
        {
            var tool = (ManagedMcpClientTool)(Activator.CreateInstance(typeof(ManagedMcpClientTool),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [new Tool { Name = "search", InputSchema = JsonElement.Parse("""{"type":"object","properties":{}}""") }, null, "search"], null)
                ?? throw new AssertionException("Missing managed tool constructor."));
            return new McpChatFunction(tool);
        }

        /// <summary>
        /// Calls the production outer approval and execution path with bounded test cancellation.
        /// </summary>
        public Task<FunctionResultContent> InvokeAsync()
        {
            var method = typeof(ChatService).GetMethod("InvokeFunctionAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing invocation method.");
            return (Task<FunctionResultContent>)(method.Invoke(_service,
                [_mixin, Context.FunctionCallContent, Context, null, _timeout.Token])
                ?? throw new AssertionException("Missing invocation task."));
        }

        /// <summary>
        /// Waits for the UI-bound projection before inspecting the actual consent request.
        /// </summary>
        public async Task<ChatPluginUserInterfaceConsentRequestItem> GetRequestAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            return Context.ChatContext.UserInterfaceBroker.ChatPluginUserInterfaceItems.OfType<ChatPluginUserInterfaceConsentRequestItem>().Single();
        }

        /// <inheritdoc />
        public Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            ExecutionCount++;
            context.Result = new FunctionResult(context.Function, "approved");
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _timeout.Cancel();
            Context.Dispose();
            Context.ChatContext.Dispose();
            Context.ChatPlugin.Dispose();
            _plugin.Dispose();
            _mixin.Dispose();
            _timeout.Dispose();
        }
    }

    /// <summary>
    /// Provides a plugin-owned identity for approval tests.
    /// </summary>
    private sealed class TestPlugin() : ModChatPlugin("consent")
    {
        /// <inheritdoc />
        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Consent tool");

        /// <inheritdoc />
        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Consent test");
    }
}