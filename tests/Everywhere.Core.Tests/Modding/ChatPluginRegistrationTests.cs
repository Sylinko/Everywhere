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
using Lucide.Avalonia;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Exercises the revocation boundary used by collectible chat tools and retained chat scopes.
/// </summary>
public sealed class ChatPluginRegistrationTests
{
    /// <summary>
    /// Closing discovery also closes previously captured bindings and waits for admitted calls to finish.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterChatPlugin_DisposeDuringCall_DrainsAndRevokesOldBinding()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = CreateManager(services);
        using var plugin = new BlockingPlugin();
        var registration = manager.RegisterChatPlugin(plugin);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(manager.NativePlugins.Single(), Is.SameAs(plugin));
        Assert.That(plugin.Key, Is.EqualTo("mod.blocking"));

        await using var chat = new RootChatContext(new ChatData(), null);
        var configuration = new AdvancedAssistantConfiguration();
        var scope = await manager.CreateScopeAsync(configuration, chat, chat.Data.RootNode, null, CancellationToken.None);
        Assert.That(scope.TryGetBinding("Invoke", out var binding, out _), Is.True);

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
            new FunctionCallChatMessage(LucideIconKind.File, new DirectLocaleKey("test")),
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
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => binding.InvokeAsync(services, new FunctionCallContent("rejected-call", binding.Declaration.Name), CancellationToken.None));

        plugin.Continue.TrySetResult();
        var result = await invocation;
        Assert.That(result.Result?.ToString(), Is.EqualTo("50"));
        await retirement;

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

        await using var chat = new RootChatContext(new ChatData(), null);
        var scope = await manager.CreateScopeAsync(new AdvancedAssistantConfiguration(), chat, chat.Data.RootNode, null, CancellationToken.None);
        Assert.That(scope.TryGetBinding("Invoke", out var binding, out _), Is.True);

        manager.Dispose();
        await registration.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => binding!.InvokeAsync(services, new FunctionCallContent("after-dispose", binding.Declaration.Name), CancellationToken.None));
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

        await using var chat = new RootChatContext(new ChatData(), null);
        var scope = await manager.CreateScopeAsync(new AdvancedAssistantConfiguration(), chat, chat.Data.RootNode, null, CancellationToken.None);
        var builtInSnapshot = scope.Plugins.Single(plugin => plugin.Key == "builtin.blocking");
        Assert.That(builtInSnapshot.GetChatFunctions().Single(), Is.SameAs(builtIn.GetChatFunctions().Single()));
        Assert.That(scope.TryGetBinding("Echo", out var builtInBinding, out _), Is.True);
        Assert.That(scope.TryGetBinding("Invoke", out var modBinding, out _), Is.True);

        await registration.DisposeAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(manager.NativePlugins.Single(), Is.SameAs(builtIn));

        var builtInResult = await builtInBinding!.InvokeAsync(
            services,
            new FunctionCallContent("builtin-call", builtInBinding.Declaration.Name),
            CancellationToken.None);
        Assert.That(builtInResult.Result?.ToString(), Is.EqualTo("built-in"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => modBinding!.InvokeAsync(services, new FunctionCallContent("mod-call", modBinding.Declaration.Name), CancellationToken.None));
    }

    private static ChatPluginManager CreateManager(IServiceProvider services)
    {
        return new ChatPluginManager(services, [], new Settings(services), Substitute.For<IRuntimeManager>(), NullLogger<ChatPluginManager>.Instance);
    }

    private sealed class BuiltInEchoPlugin : BuiltInChatPlugin
    {
        public override bool IsDefaultEnabled => true;

        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Built-in");

        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Permanent test tool");

        public BuiltInEchoPlugin() : base("blocking")
        {
            _functionsSource.Add(new NativeChatFunction(Echo, ChatFunctionPermissions.None));
        }

        private static string Echo() => "built-in";
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
            _functionsSource.Add(new NativeChatFunction(
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
