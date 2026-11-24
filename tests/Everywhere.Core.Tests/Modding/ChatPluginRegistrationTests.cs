using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using DynamicData;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Harness;
using Everywhere.I18N;
using Tabler.Avalonia;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Exercises the revocation, drain, and wire-namespace boundary used by collectible chat tools and retained scopes.
/// </summary>
[NonParallelizable]
public sealed class ChatPluginRegistrationTests
{
    /// <summary>
    /// Closing discovery revokes previously captured bindings, short-circuits subsequent hooks,
    /// and waits for admitted calls to finish draining.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterChatPlugin_DisposeDuringCall_DrainsAndRevokesOldBinding()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var manager = CreateManager(services);
        using var plugin = new BlockingPlugin();
        var registration = manager.RegisterChatPlugin(plugin);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(manager.NativePlugins.Single(), Is.SameAs(plugin));
        Assert.That(plugin.Key, Is.EqualTo("mod.blocking"));

        await using var chat = new RootChatContext(new ChatData(), null);
        var configuration = new AdvancedAssistantConfiguration();
        var scope = await manager.CreateScopeAsync(configuration, chat, chat.Data.RootNode, null, CancellationToken.None);
        Assert.That(scope.TryGetBinding("mod__Invoke", out var binding, out _), Is.True);

        var call = new FunctionCallContent("original-call", binding!.Declaration.Name);
        Assert.That(binding.Function.BeforeCallHook?.Invoke(call), Is.TypeOf<ChatPluginTextDisplayBlock>());
        Assert.That(plugin.ObservedCall, Is.SameAs(call));

        var session = Substitute.For<ModelSession>(
            new AdvancedAssistantConfiguration(),
            new ModelConnection(ModelProviderSchema.OpenAI, "https://example.invalid", null, new HttpClient(), null));
        await using var generation = new GenerationContext(
            services,
            session,
            null,
            Substitute.For<IPromptRenderer>(),
            "constraints",
            Modalities.Text,
            80,
            -1,
            new GenerationApprovalState(ToolApprovalMode.Ask),
            true,
            chat.Data.RootNode);
        await using var consentContext = new FunctionCallContext(
            generation.Services,
            chat,
            binding,
            new FunctionCallChatMessage(TablerIconKind.File, new DirectLocaleKey("test")),
            call,
            new ObservableToolRulesets(),
            generation,
            (_, _, _) => throw new AssertionException("No review expected."));
        var consentRequest = new PermissionConsentRequest(binding.Function);
        Assert.That(binding.Function.PermissionConsentHook?.Invoke(consentContext, consentRequest), Is.Null);
        Assert.That(consentRequest.HeaderKey.ToString(), Is.EqualTo("Custom approval"));
        Assert.That(plugin.HookCalls, Is.EqualTo(2));
        Assert.That(binding.Declaration.JsonSchema.ToString(), Does.Contain("count"));

        var invocation = binding.InvokeAsync(services, call, CancellationToken.None);
        await plugin.Entered.Task;

        var retirement = registration.DisposeAsync().AsTask();
        Assert.That(retirement.IsCompleted, Is.False);
        Assert.That(binding.Function.BeforeCallHook?.Invoke(call), Is.Null);
        Assert.That(binding.Function.PermissionConsentHook?.Invoke(consentContext, consentRequest), Is.False);
        Assert.That(plugin.HookCalls, Is.EqualTo(2), "Retirement must not re-enter either plugin hook.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => binding.InvokeAsync(
            services,
            new FunctionCallContent("rejected-call", binding.Declaration.Name),
            CancellationToken.None));

        plugin.Continue.TrySetResult();
        var result = await invocation;
        Assert.That(result.Result?.ToString(), Is.EqualTo("50"));
        await retirement;

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(manager.NativePlugins, Is.Empty);
        await registration.DisposeAsync();
    }

    /// <summary>
    /// Hooks execute outside internal registration locks and participate in retirement draining so
    /// unloading waits for active hooks without lock-order deadlock.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterChatPlugin_DisposeDuringHook_DrainsWithoutDeadlock()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var manager = CreateManager(services);
        using var hookEntered = new ManualResetEventSlim();
        using var releaseHook = new ManualResetEventSlim();
        using var plugin = new HookBlockingPlugin(hookEntered, releaseHook);
        var registration = manager.RegisterChatPlugin(plugin);

        await using var chat = new RootChatContext(new ChatData(), null);
        var scope = await manager.CreateScopeAsync(new AdvancedAssistantConfiguration(), chat, chat.Data.RootNode, null, CancellationToken.None);
        Assert.That(scope.TryGetBinding("mod__Run", out var binding, out _), Is.True);

        var hookTask = Task.Run(() => binding!.Function.BeforeCallHook?.Invoke(new FunctionCallContent("hook-call", binding.Declaration.Name)));
        Assert.That(hookEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);

        var retirement = Task.Run(async () => await registration.DisposeAsync());
        await Task.Delay(30);
        Assert.That(retirement.IsCompleted, Is.False);

        releaseHook.Set();
        var displayBlock = await hookTask;
        await retirement;

        Assert.That(displayBlock, Is.TypeOf<ChatPluginTextDisplayBlock>());
        Assert.That(binding!.Function.BeforeCallHook?.Invoke(new FunctionCallContent("after-close", binding.Declaration.Name)), Is.Null);
    }

    /// <summary>
    /// Ordinary DI exit may dispose discovery before an entry releases its registration.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterChatPlugin_ManagerDisposedFirst_StillClosesRegistration()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var manager = CreateManager(services);
        using var plugin = new BlockingPlugin();
        var registration = manager.RegisterChatPlugin(plugin);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        await using var chat = new RootChatContext(new ChatData(), null);
        var scope = await manager.CreateScopeAsync(new AdvancedAssistantConfiguration(), chat, chat.Data.RootNode, null, CancellationToken.None);
        Assert.That(scope.TryGetBinding("mod__Invoke", out var binding, out _), Is.True);

        await manager.DisposeAsync();
        await registration.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => binding!.InvokeAsync(
            services,
            new FunctionCallContent("after-dispose", binding.Declaration.Name),
            CancellationToken.None));
    }

    /// <summary>
    /// Built-in tools keep raw names and permanent lifetime, while mod tools receive deterministic
    /// <c>mod__</c> wire names independent of registration order and retire independently.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterChatPlugin_WithSameNamedBuiltInAndMods_AppliesModNamespaceAndDeterministicOrder()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var builtIn = new BuiltInSearchPlugin();
        await using var manager = new ChatPluginManager(
            services,
            [builtIn],
            new Settings(services),
            Substitute.For<IRuntimeManager>(),
            NullLogger<ChatPluginManager>.Instance);

        using var betaMod = new ModSearchPlugin("beta");
        using var alphaMod = new ModSearchPlugin("alpha");
        // Register beta before alpha intentionally to assert Key-ordered deterministic deduplication.
        await using var betaRegistration = manager.RegisterChatPlugin(betaMod);
        var alphaRegistration = manager.RegisterChatPlugin(alphaMod);
        try
        {
            await using var chat = new RootChatContext(new ChatData(), null);
            var scope = await manager.CreateScopeAsync(new AdvancedAssistantConfiguration(), chat, chat.Data.RootNode, null, CancellationToken.None);
            var builtInSnapshot = scope.Plugins.Single(plugin => plugin.Key == "builtin.search");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(builtInSnapshot, Is.TypeOf<BuiltInChatPluginSnapshot>());
                Assert.That(builtInSnapshot.Functions.Single(), Is.SameAs(builtIn.GetChatFunctions().Single()));
                Assert.That(scope.TryGetBinding("search_files", out var builtInBinding, out _), Is.True);
                Assert.That(builtInBinding?.Plugin.Key, Is.EqualTo("builtin.search"));
                Assert.That(scope.TryGetBinding("mod__search_files", out var alphaBinding, out _), Is.True);
                Assert.That(alphaBinding?.Plugin, Is.TypeOf<ModChatPluginSnapshot>());
                Assert.That(alphaBinding?.Plugin.Key, Is.EqualTo("mod.alpha"));
                Assert.That(scope.TryGetBinding("mod__search_files_1", out var betaBinding, out _), Is.True);
                Assert.That(betaBinding?.Plugin.Key, Is.EqualTo("mod.beta"));
            }

            scope.TryGetBinding("search_files", out var builtInTarget, out _);
            scope.TryGetBinding("mod__search_files", out var alphaTarget, out _);

            await alphaRegistration.DisposeAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.That(manager.NativePlugins, Is.EquivalentTo(new NativeChatPlugin[] { builtIn, betaMod }));

            var builtInResult = await builtInTarget!.InvokeAsync(
                services,
                new FunctionCallContent("builtin-call", builtInTarget.Declaration.Name),
                CancellationToken.None);
            Assert.That(builtInResult.Result?.ToString(), Is.EqualTo("built-in"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => alphaTarget!.InvokeAsync(
                services,
                new FunctionCallContent("mod-call", alphaTarget.Declaration.Name),
                CancellationToken.None));

            var nextScope = await manager.CreateScopeAsync(
                new AdvancedAssistantConfiguration(),
                chat,
                chat.Data.RootNode,
                null,
                CancellationToken.None);
            Assert.That(nextScope.TryGetBinding("mod__search_files", out var promotedBetaBinding, out _), Is.True);
            Assert.That(promotedBetaBinding?.Plugin.Key, Is.EqualTo("mod.beta"));
        }
        finally
        {
            await alphaRegistration.DisposeAsync();
        }
    }

    private static ChatPluginManager CreateManager(IServiceProvider services)
    {
        return new ChatPluginManager(services, [], new Settings(services), Substitute.For<IRuntimeManager>(), NullLogger<ChatPluginManager>.Instance);
    }

    private sealed class BuiltInSearchPlugin : BuiltInChatPlugin
    {
        public override bool IsDefaultEnabled => true;

        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Built-in");

        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Permanent test tool");

        public BuiltInSearchPlugin() : base("search")
        {
            _functionsSource.Add(new NativeChatFunction(SearchFiles, ChatFunctionPermissions.None, functionName: "search_files"));
        }

        private static string SearchFiles() => "built-in";
    }

    private sealed class ModSearchPlugin : ModChatPlugin
    {
        public override bool IsDefaultEnabled => true;

        public override IDynamicLocaleKey HeaderKey { get; }

        public override IDynamicLocaleKey DescriptionKey { get; }

        public ModSearchPlugin(string name) : base(name)
        {
            HeaderKey = new DirectLocaleKey(name);
            DescriptionKey = new DirectLocaleKey($"Mod search ({name})");
            _functionsSource.Add(new NativeChatFunction(() => $"mod:{name}", ChatFunctionPermissions.None, functionName: "search_files"));
        }
    }

    private sealed class HookBlockingPlugin : ModChatPlugin
    {
        public override bool IsDefaultEnabled => true;

        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("HookBlocking");

        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Hook blocking test tool");

        internal HookBlockingPlugin(ManualResetEventSlim hookEntered, ManualResetEventSlim releaseHook) : base("hook_blocking")
        {
            _functionsSource.Add(
                new NativeChatFunction(
                    Run,
                    ChatFunctionPermissions.None,
                    beforeCallHook: _ =>
                    {
                        hookEntered.Set();
                        releaseHook.Wait(TimeSpan.FromSeconds(5));
                        return new ChatPluginTextDisplayBlock("Hook finished");
                    }));
        }

        private static string Run() => "ok";
    }

    private sealed class BlockingPlugin : ModChatPlugin
    {
        public override bool IsDefaultEnabled => true;

        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Blocking");

        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Test tool");

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FunctionCallContent? ObservedCall { get; private set; }

        internal int HookCalls { get; private set; }

        internal BlockingPlugin() : base("blocking")
        {
            _functionsSource.Add(
                new NativeChatFunction(
                    InvokeAsync,
                    ChatFunctionPermissions.FileRead,
                    beforeCallHook: content =>
                    {
                        HookCalls++;
                        ObservedCall = content;
                        return new ChatPluginTextDisplayBlock("Preview");
                    },
                    permissionConsentHook: (context, request) =>
                    {
                        HookCalls++;
                        ObservedCall = context.FunctionCallContent;
                        request.HeaderKey = new DirectLocaleKey("Custom approval");
                        return null;
                    }));
        }

        private async Task<string> InvokeAsync(int count = 50)
        {
            Entered.TrySetResult();
            await Continue.Task;
            return count.ToString();
        }
    }
}
