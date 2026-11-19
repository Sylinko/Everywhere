using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using DynamicData;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Core.Tests.Modding.Fixtures;
using Everywhere.Harness;
using Everywhere.I18N;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Exercises the revocable MEAI <see cref="AIFunction"/> proxy through Harness scopes and collectible ownership.
/// </summary>
public sealed class ChatPluginProxyTests
{
    /// <summary>
    /// Complex JSON, defaults, and injected services still flow through MEAI's original binding,
    /// while declaration metadata stays isolated and raw result identity is preserved.
    /// </summary>
    [AvaloniaTest]
    public async Task Snapshot_ComplexArguments_PreservesServiceInjectionAndResultIdentity()
    {
        var marker = new object();
        using var services = new ServiceCollection().AddSingleton(marker).BuildServiceProvider();
        using var manager = CreateManager(services);
        using var plugin = new PipelinePlugin();
        await using var registration = manager.RegisterChatPlugin(plugin);

        await using var chat = new RootChatContext(new ChatData(), null);
        var scope = await manager.CreateScopeAsync(new AdvancedAssistantConfiguration(), chat, chat.Data.RootNode, null, CancellationToken.None);
        Assert.That(scope.TryGetBinding("Echo", out var binding, out _), Is.True);

        var call = new FunctionCallContent(
            "call-1",
            binding!.Declaration.Name,
            new Dictionary<string, object?>
            {
                ["request"] = JsonElement.Parse("""{"Location":{"Directory":"changed"},"Tags":["a","b"]}""")
            });

        var result = await binding.InvokeAsync(services, call, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(plugin.Observed, Is.EqualTo("changed:a,b:20"));
            Assert.That(plugin.ObservedService, Is.SameAs(marker));
            Assert.That(result.CallId, Is.EqualTo("call-1"));
            Assert.That(result.Result, Is.SameAs(plugin.Payload));
            Assert.That(binding.Declaration, Is.Not.InstanceOf<AIFunction>());
            Assert.That(binding.Function.Function.UnderlyingMethod, Is.Null);
            Assert.That(binding.Function.Function.JsonSerializerOptions, Is.SameAs(AIJsonUtilities.DefaultOptions));
            Assert.That(binding.Declaration.JsonSchema.ToString(), Does.Contain("location"));
        });
    }

    /// <summary>
    /// Old scopes, bindings, and stored results survive collection without pinning collectible ALCs,
    /// and cannot invoke a later activation.
    /// </summary>
    [AvaloniaTest]
    public async Task Snapshot_CollectiblePluginRetired_CollectsWhileSnapshotAndResultRemainAlive()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = CreateManager(services);
        var retained = await InvokeAndRetireCollectibleAsync(manager, services);

        for (var attempt = 0; attempt < 20 && retained.Owner.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(25);
        }

        Assert.That(retained.Owner.IsAlive, Is.False, "Retained scopes and results must not pin collectible CLR metadata.");
        Assert.That(retained.Binding.Declaration.JsonSchema.ToString(), Does.Contain("location"));
        Assert.That(retained.Result.Result, Is.EqualTo("docs:a,b:20"));
        var arguments = new Dictionary<string, object?>
        {
            ["request"] = JsonElement.Parse("""{"location":{"directory":"new"},"tags":[]}""")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => retained.Binding.InvokeAsync(services, new FunctionCallContent("retired", retained.Binding.Declaration.Name, arguments), CancellationToken.None));
        Assert.That(manager.NativePlugins, Is.Empty);

        using var replacement = new CollectibleChatPlugin();
        await using var registration = manager.RegisterChatPlugin(replacement);
        await using var chat = new RootChatContext(new ChatData(), null);
        var newScope = await manager.CreateScopeAsync(new AdvancedAssistantConfiguration(), chat, chat.Data.RootNode, null, CancellationToken.None);
        Assert.That(newScope.TryGetBinding("Search", out var newBinding, out _), Is.True);
        var replacementResult = await newBinding!.InvokeAsync(
            services,
            new FunctionCallContent("new-call", newBinding.Declaration.Name, arguments),
            CancellationToken.None);
        Assert.That(replacementResult.Result, Is.EqualTo("new::20"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => retained.Binding.InvokeAsync(services, new FunctionCallContent("still-retired", retained.Binding.Declaration.Name, arguments), CancellationToken.None));

        GC.KeepAlive(retained);
        GC.KeepAlive(retained.Registration);
    }

    /// <summary>
    /// Original tool exceptions and pre-cancelled tokens propagate intact through the proxy.
    /// </summary>
    [AvaloniaTest]
    public async Task Snapshot_FailuresAndCancellation_PropagateThroughProxy()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = CreateManager(services);
        using var plugin = new PipelinePlugin();
        await using var registration = manager.RegisterChatPlugin(plugin);

        await using var chat = new RootChatContext(new ChatData(), null);
        var scope = await manager.CreateScopeAsync(new AdvancedAssistantConfiguration(), chat, chat.Data.RootNode, null, CancellationToken.None);
        Assert.That(scope.TryGetBinding("Fail", out var failBinding, out _), Is.True);
        Assert.That(scope.TryGetBinding("Value", out var valueBinding, out _), Is.True);

        var failure = await Assert.ThrowsAsync<ApplicationException>(
            () => failBinding!.InvokeAsync(services, new FunctionCallContent("fail-call", failBinding.Declaration.Name), CancellationToken.None));
        Assert.That(failure, Is.SameAs(plugin.Failure));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => failBinding!.InvokeAsync(services, new FunctionCallContent("cancel-call", failBinding.Declaration.Name), cancellation.Token));

        var valueResult = await valueBinding!.InvokeAsync(services, new FunctionCallContent("ok-call", valueBinding.Declaration.Name), CancellationToken.None);
        Assert.That(valueResult.Result, Is.EqualTo("value"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<RetainedChat> InvokeAndRetireCollectibleAsync(ChatPluginManager manager, IServiceProvider services)
    {
        var owner = new AssemblyLoadContext("chat-proxy-test", isCollectible: true);
        var assembly = owner.LoadFromAssemblyPath(typeof(ChatPluginProxyTests).Assembly.Location);
        var plugin = (ModChatPlugin)Activator.CreateInstance(
            assembly.GetType("Everywhere.Core.Tests.Modding.Fixtures.CollectibleChatPlugin", throwOnError: true)!)!;
        var registration = manager.RegisterChatPlugin(plugin);

        await using var chat = new RootChatContext(new ChatData(), null);
        var scope = await manager.CreateScopeAsync(new AdvancedAssistantConfiguration(), chat, chat.Data.RootNode, null, CancellationToken.None);
        Assert.That(scope.TryGetBinding("Search", out var binding, out _), Is.True);

        var call = new FunctionCallContent(
            "collectible-call",
            binding!.Declaration.Name,
            new Dictionary<string, object?>
            {
                ["request"] = JsonElement.Parse("""{"location":{"directory":"docs"},"tags":["a","b"]}""")
            });
        var result = await binding.InvokeAsync(services, call, CancellationToken.None);

        await registration.DisposeAsync();
        plugin.Dispose();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var weakOwner = new WeakReference(owner);
        owner.Unload();
        return new RetainedChat(weakOwner, scope, binding, result, registration);
    }

    private static ChatPluginManager CreateManager(IServiceProvider services)
    {
        return new ChatPluginManager(services, [], new Settings(services), Substitute.For<IRuntimeManager>(), NullLogger<ChatPluginManager>.Instance);
    }

    private sealed record RetainedChat(
        WeakReference Owner,
        IChatPluginScope Scope,
        ToolBinding Binding,
        FunctionResultContent Result,
        IAsyncDisposable Registration);

    private sealed class PipelinePlugin : ModChatPlugin
    {
        public override bool IsDefaultEnabled => true;

        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Pipeline");

        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Pipeline tests");

        internal object Payload { get; } = new();

        internal ApplicationException Failure { get; } = new("original failure");

        internal string? Observed { get; private set; }

        internal object? ObservedService { get; private set; }

        internal PipelinePlugin() : base("pipeline")
        {
            _functionsSource.Add(new NativeChatFunction(Echo, ChatFunctionPermissions.FileRead));
            _functionsSource.Add(new NativeChatFunction(Value, ChatFunctionPermissions.FileRead));
            _functionsSource.Add(new NativeChatFunction(Fail, ChatFunctionPermissions.FileRead));
        }

        private object Echo(SearchRequest request, [FromChatServices] object service, int limit = 20)
        {
            Observed = $"{request.Location.Directory}:{string.Join(',', request.Tags)}:{limit}";
            ObservedService = service;
            return Payload;
        }

        private static string Value() => "value";

        private string Fail() => throw Failure;
    }
}
