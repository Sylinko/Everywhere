using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using DynamicData;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.I18N;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using NSubstitute;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Exercises the revocation boundary used by collectible chat tools and existing chat snapshots.
/// </summary>
public sealed class ChatPluginRegistrationTests
{
    /// <summary>
    /// Closing discovery also closes previously copied functions and waits for an admitted call to finish.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterChatPlugin_DisposeDuringCall_DrainsAndRevokesOldFunction()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = CreateManager(services);
        using var plugin = new BlockingPlugin();
        var registration = manager.RegisterChatPlugin(plugin);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(manager.NativePlugins.Single(), Is.SameAs(plugin));
        Assert.That(plugin.Key, Is.EqualTo("mod.blocking"));
        using var chat = new ChatContext();
        var scope = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        var function = scope.Plugins.Single().Single().Clone();
        var chatFunction = (NativeChatFunction)scope.Plugins.Single().GetChatFunctions().Single();
        var call = new FunctionCallContent(function.Name, id: "original-call");
        Assert.That(chatFunction.BeforeCallHook?.Invoke(call), Is.TypeOf<ChatPluginTextDisplayBlock>());
        Assert.That(plugin.ObservedCall, Is.SameAs(call));
        using var mixin = Substitute.For<KernelMixin>(new AdvancedAssistantConfiguration(),
            new ModelConnection(ModelProviderSchema.OpenAI, "https://example.invalid", null, new HttpClient(), null));
        var generation = new GenerationContext(new Kernel(), mixin, Substitute.For<IPromptRenderer>(), "constraints",
            Modalities.Text, 80, -1, new GenerationApprovalState(ToolApprovalMode.Ask), true);
        using var consentContext = new FunctionCallContext(generation.Kernel, chat, scope.Plugins.Single(), chatFunction,
            new FunctionCallChatMessage(LucideIconKind.File, new DirectLocaleKey("test")), call,
            new ObservableToolRulesets(), generation, (_, _, _) => throw new AssertionException("No review expected."));
        var consentRequest = new PermissionConsentRequest(chatFunction);
        Assert.That(chatFunction.PermissionConsentHook?.Invoke(consentContext, consentRequest), Is.Null);
        Assert.That(consentRequest.HeaderKey.ToString(), Is.EqualTo("Custom approval"));
        Assert.That(plugin.HookCalls, Is.EqualTo(2));
        var parameter = function.Metadata.Parameters.Single();
        Assert.That(parameter.DefaultValue, Is.EqualTo(plugin.GetChatFunctions().Single().KernelFunction.Metadata.Parameters.Single().DefaultValue));
        Assert.That(parameter.ParameterType, Is.Null);
        Assert.That(parameter.IsRequired, Is.False);

        var kernel = new Kernel();
        var filter = new CountingFilter();
        kernel.FunctionInvocationFilters.Add(filter);
        var invocation = function.InvokeAsync(kernel);
        await plugin.Entered.Task;
        var retirement = registration.DisposeAsync().AsTask();
        Assert.That(retirement.IsCompleted, Is.False);
        Assert.That(chatFunction.BeforeCallHook?.Invoke(call), Is.Null);
        Assert.That(chatFunction.PermissionConsentHook?.Invoke(consentContext, consentRequest), Is.False);
        Assert.That(plugin.HookCalls, Is.EqualTo(2), "Retirement must not re-enter either plugin hook.");
        Assert.ThrowsAsync<InvalidOperationException>(async () => await function.InvokeAsync(kernel));
        plugin.Continue.TrySetResult();
        Assert.That((await invocation).GetValue<string>(), Is.EqualTo("50"));
        await retirement;
        Assert.That(filter.Count, Is.EqualTo(1), "Retired calls are rejected before entering the filter pipeline.");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(manager.NativePlugins, Is.Empty);
        await registration.DisposeAsync();
    }

    /// <summary>
    /// Ordinary DI exit may dispose discovery before an entry releases its registration.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterChatPlugin_ManagerDisposedFirst_StillClosesRegistration()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var manager = CreateManager(services);
        using var plugin = new BlockingPlugin();
        var registration = manager.RegisterChatPlugin(plugin);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        using var chat = new ChatContext();
        var scope = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        var function = scope.Plugins.Single().Single().Clone();
        manager.Dispose();
        await registration.DisposeAsync();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await function.InvokeAsync(new Kernel()));
    }

    /// <summary>
    /// Built-in tools keep their ordinary functions and lifetime when a same-named mod tool retires.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterChatPlugin_WithSameNamedBuiltIn_RetiresOnlyModFunctions()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var builtIn = new BuiltInEchoPlugin();
        using var manager = new ChatPluginManager(
            services, [builtIn], new Settings(services), Substitute.For<IRuntimeManager>(), NullLogger<ChatPluginManager>.Instance);
        using var mod = new BlockingPlugin();
        await using var registration = manager.RegisterChatPlugin(mod);
        using var chat = new ChatContext();
        var scope = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        var builtInSnapshot = scope.Plugins.Single(plugin => plugin.Key == "builtin.blocking");
        var modFunction = scope.Plugins.Single(plugin => plugin.Key == "mod.blocking").Single();
        Assert.That(builtInSnapshot.GetChatFunctions().Single(), Is.SameAs(builtIn.GetChatFunctions().Single()));

        await registration.DisposeAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(manager.NativePlugins.Single(), Is.SameAs(builtIn));
        Assert.That((await builtInSnapshot.Single().InvokeAsync(new Kernel())).GetValue<string>(), Is.EqualTo("built-in"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await modFunction.InvokeAsync(new Kernel()));
    }

    /// <summary>
    /// Uses the production manager without unrelated runtimes or MCP connections.
    /// </summary>
    private static ChatPluginManager CreateManager(IServiceProvider services)
    {
        return new ChatPluginManager(services, [], new Settings(services), Substitute.For<IRuntimeManager>(), NullLogger<ChatPluginManager>.Instance);
    }

    /// <summary>
    /// Supplies a permanent tool with the same model-facing name as the revocable fixture.
    /// </summary>
    private sealed class BuiltInEchoPlugin : BuiltInChatPlugin
    {
        /// <inheritdoc />
        public override bool IsDefaultEnabled => true;

        /// <inheritdoc />
        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Built-in");

        /// <inheritdoc />
        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Permanent test tool");

        public BuiltInEchoPlugin() : base("blocking")
        {
            _functionsSource.Add(new NativeChatFunction(Echo, ChatFunctionPermissions.None));
        }

        private static string Echo() => "built-in";
    }

    /// <summary>
    /// Holds a real Semantic Kernel call open until the test releases it.
    /// </summary>
    private sealed class BlockingPlugin : ModChatPlugin
    {
        /// <inheritdoc />
        public override bool IsDefaultEnabled => true;

        /// <inheritdoc />
        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Blocking");

        /// <inheritdoc />
        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Test tool");

        /// <summary>
        /// Signals that a call was admitted before disposal.
        /// </summary>
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Allows the admitted call to complete.
        /// </summary>
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets the original call received by the display hook, including calls with no arguments.
        /// </summary>
        internal FunctionCallContent? ObservedCall { get; private set; }

        /// <summary>
        /// Counts admitted hook executions to detect calls after registration closes.
        /// </summary>
        internal int HookCalls { get; private set; }

        /// <summary>
        /// Publishes a function with a meaningful optional parameter.
        /// </summary>
        internal BlockingPlugin() : base("blocking")
        {
            _functionsSource.Add(new NativeChatFunction(
                InvokeAsync,
                ChatFunctionPermissions.FileRead,
                permissionConsentHook: (context, request) =>
                {
                    HookCalls++;
                    ObservedCall = context.FunctionCallContent;
                    request.HeaderKey = new DirectLocaleKey("Custom approval");
                    return null;
                },
                beforeCallHook: content =>
                {
                    HookCalls++;
                    ObservedCall = content;
                    return new ChatPluginTextDisplayBlock("Preview");
                }));
        }

        /// <summary>
        /// Demonstrates preservation of defaults through the host wrapper.
        /// </summary>
        private async Task<string> InvokeAsync(int count = 50)
        {
            Entered.TrySetResult();
            await Continue.Task;
            return count.ToString();
        }
    }

    /// <summary>
    /// Detects accidental double invocation of the caller's Semantic Kernel filters.
    /// </summary>
    private sealed class CountingFilter : IFunctionInvocationFilter
    {
        /// <summary>
        /// Gets how often the caller's invocation pipeline ran.
        /// </summary>
        internal int Count { get; private set; }

        /// <inheritdoc />
        public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            Count++;
            await next(context);
        }
    }
}
