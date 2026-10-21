using System.Globalization;
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
using Everywhere.I18N;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using NSubstitute;
using AIFunction = Microsoft.Extensions.AI.AIFunction;
using AIFunctionArguments = Microsoft.Extensions.AI.AIFunctionArguments;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Exercises the patched SK pipeline through production chat snapshots, including collectible ownership.
/// </summary>
public sealed class ChatPluginProxyTests
{
    /// <summary>
    /// Complex JSON, defaults, injected services and function aliases still use SK's original binding.
    /// Result payload and metadata retain their identity, rather than being converted by the proxy.
    /// </summary>
    [AvaloniaTest]
    public async Task Snapshot_ComplexArguments_PreservesPipelineAndResultIdentity()
    {
        var marker = new object();
        using var services = new ServiceCollection().AddSingleton(marker).BuildServiceProvider();
        using var manager = CreateManager(services);
        using var plugin = new PipelinePlugin();
        await using var registration = manager.RegisterChatPlugin(plugin);
        using var chat = new ChatContext();
        var scope = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        var function = scope.Plugins.Single().Single(item => item.Name == "Echo").Clone("alias_plugin");
        function.Metadata.Name = "alias_echo";
        var kernel = new Kernel(services) { Culture = CultureInfo.GetCultureInfo("fr-FR") };
        var seen = new List<string>();
        kernel.FunctionInvocationFilters.Add(new CallbackFilter(async (context, next) =>
        {
            seen.Add("before:" + context.Function.Name);
            Assert.That(context.Kernel, Is.SameAs(kernel));
            context.Arguments["request"] = JsonElement.Parse("""{"Location":{"Directory":"changed"},"Tags":["a","b"]}""");
            await next(context);
            Assert.That(context.Result.Function, Is.SameAs(function));
            seen.Add("after:" + context.Function.Name);
        }));

        var result = await function.InvokeAsync(kernel, new KernelArguments());

        Assert.Multiple(() =>
        {
            Assert.That(plugin.Observed, Is.EqualTo("changed:a,b:20:alias_plugin:alias_echo"));
            Assert.That(plugin.ObservedKernel, Is.SameAs(kernel));
            Assert.That(plugin.ObservedService, Is.SameAs(marker));
            Assert.That(result.GetValue<object>(), Is.SameAs(plugin.Payload));
            Assert.That(result.Metadata, Is.SameAs(plugin.ResultMetadata));
            Assert.That(result.Culture, Is.EqualTo(kernel.Culture));
            Assert.That(result.Function, Is.SameAs(function));
            Assert.That(seen, Is.EqualTo(new[] { "before:alias_echo", "after:alias_echo" }));
            Assert.That(function.Metadata.Parameters.All(parameter => parameter.ParameterType is null), Is.True);
            Assert.That(function.Metadata.Parameters.First().Schema?.ToString(), Does.Contain("Location"));
        });
    }

    /// <summary>
    /// A post-invocation filter remains part of the admitted operation during retirement.
    /// </summary>
    [AvaloniaTest]
    public async Task Snapshot_DisposeDuringPostFilter_WaitsUntilFilterReturns()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = CreateManager(services);
        using var plugin = new PipelinePlugin();
        var registration = manager.RegisterChatPlugin(plugin);
        using var chat = new ChatContext();
        var scope = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        var function = scope.Plugins.Single().Single(item => item.Name == "Value");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kernel = new Kernel();
        kernel.FunctionInvocationFilters.Add(new CallbackFilter(async (context, next) =>
        {
            await next(context);
            entered.SetResult();
            await proceed.Task;
        }));

        var invocation = function.InvokeAsync(kernel);
        await entered.Task;
        var retirement = registration.DisposeAsync().AsTask();
        Assert.That(retirement.IsCompleted, Is.False);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await function.InvokeAsync(kernel));
        proceed.SetResult();
        Assert.That((await invocation).GetValue<string>(), Is.EqualTo("value"));
        await retirement;
    }

    /// <summary>
    /// Streaming ownership includes consumer pauses and early enumerator disposal.
    /// </summary>
    [AvaloniaTest]
    public async Task Snapshot_DisposeDuringStream_WaitsForEnumeratorDisposal()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = CreateManager(services);
        using var plugin = new PipelinePlugin();
        var registration = manager.RegisterChatPlugin(plugin);
        using var chat = new ChatContext();
        var scope = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        var function = scope.Plugins.Single().Single(item => item.Name == "Stream");
        var kernel = new Kernel();
        var filterCalls = 0;
        kernel.FunctionInvocationFilters.Add(new CallbackFilter(async (context, next) =>
        {
            filterCalls++;
            await next(context);
        }));
        var enumerator = function.InvokeStreamingAsync<int>(kernel).GetAsyncEnumerator();
        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(1));
        var retirement = registration.DisposeAsync().AsTask();
        Assert.That(retirement.IsCompleted, Is.False);
        await enumerator.DisposeAsync();
        await retirement;
        Assert.That(plugin.IsStreamDisposed, Is.True);
        Assert.That(filterCalls, Is.EqualTo(1));
    }

    /// <summary>
    /// Old snapshots and stored FunctionResultContent survive collection, but cannot call a replacement activation.
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

        Assert.That(retained.Owner.IsAlive, Is.False, "The old chat snapshot and result must not retain collectible CLR metadata.");
        var function = retained.Scope.Plugins.Single().Single();
        Assert.That(function.Metadata.Parameters.First().Schema?.ToString(), Does.Contain("location"));
        Assert.That(retained.Result.Result, Is.EqualTo("docs:a,b:20"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await function.InvokeAsync(new Kernel(services)));
        Assert.That(manager.NativePlugins, Is.Empty);

        using var replacement = new CollectibleChatPlugin();
        await using var registration = manager.RegisterChatPlugin(replacement);
        using var chat = new ChatContext();
        var newScope = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        var arguments = new KernelArguments
        {
            ["request"] = JsonElement.Parse("""{"location":{"directory":"new"},"tags":[]}""")
        };
        Assert.That((await newScope.Plugins.Single().Single().InvokeAsync(new Kernel(services), arguments)).GetValue<string>(), Is.EqualTo("new::20"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await function.InvokeAsync(new Kernel(services), arguments));
        GC.KeepAlive(retained);
        GC.KeepAlive(retained.Registration);
    }

    /// <summary>
    /// Filter short-circuiting, original exceptions and SK cancellation wrapping remain intact.
    /// </summary>
    [AvaloniaTest]
    public async Task Snapshot_FiltersAndFailures_PreserveSemanticKernelBehavior()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = CreateManager(services);
        using var plugin = new PipelinePlugin();
        await using var registration = manager.RegisterChatPlugin(plugin);
        using var chat = new ChatContext();
        var scope = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        var function = scope.Plugins.Single().Single(item => item.Name == "Fail");
        var kernel = new Kernel();
        kernel.FunctionInvocationFilters.Add(new CallbackFilter((context, _) =>
        {
            context.Result = new FunctionResult(context.Function, plugin.Payload);
            return Task.CompletedTask;
        }));
        Assert.That((await function.InvokeAsync(kernel)).GetValue<object>(), Is.SameAs(plugin.Payload));
        kernel.FunctionInvocationFilters.Clear();
        var failure = Assert.ThrowsAsync<ApplicationException>(async () => await function.InvokeAsync(kernel));
        Assert.That(failure, Is.SameAs(plugin.Failure));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAsync<KernelFunctionCanceledException>(async () => await function.InvokeAsync(kernel, cancellationToken: cancellation.Token));
        Assert.That((await scope.Plugins.Single().Single(item => item.Name == "Value").InvokeAsync(kernel)).GetValue<string>(), Is.EqualTo("value"));
    }

    /// <summary>
    /// The AIFunction entry keeps SK's own JSON result handling and kernel resolution.
    /// </summary>
    [AvaloniaTest]
    public async Task Snapshot_AIFunctionEntry_UsesOriginalExecutionAndRevocation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = CreateManager(services);
        using var plugin = new PipelinePlugin();
        var registration = manager.RegisterChatPlugin(plugin);
        using var chat = new ChatContext();
        var scope = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        var kernel = new Kernel(services);
        var function = (AIFunction)scope.Plugins.Single().Single(item => item.Name == "Value");
        var arguments = new AIFunctionArguments();
        var result = await function.InvokeAsync(kernel, arguments);
        Assert.That(result, Is.TypeOf<JsonElement>());
        Assert.That(((JsonElement)result).GetString(), Is.EqualTo("value"));
        await registration.DisposeAsync();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await function.InvokeAsync(kernel, arguments));
    }

    /// <summary>
    /// Leaves no collectible locals in the GC-observation frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<RetainedChat> InvokeAndRetireCollectibleAsync(ChatPluginManager manager, IServiceProvider services)
    {
        var owner = new AssemblyLoadContext("chat-proxy-test", isCollectible: true);
        var assembly = owner.LoadFromAssemblyPath(typeof(ChatPluginProxyTests).Assembly.Location);
        var plugin = (ModChatPlugin)Activator.CreateInstance(
            assembly.GetType("Everywhere.Core.Tests.Modding.Fixtures.CollectibleChatPlugin", throwOnError: true)!)!;
        var registration = manager.RegisterChatPlugin(plugin);
        using var chat = new ChatContext();
        var scope = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        var kernel = new Kernel(services);
        kernel.Plugins.Add(scope.Plugins.Single());
        var content = new FunctionCallContent("Search", arguments: new KernelArguments
        {
            ["request"] = JsonElement.Parse("""{"location":{"directory":"docs"},"tags":["a","b"]}""")
        });
        var result = await content.InvokeAsync(kernel);
        Assert.That(((FunctionResult)result.InnerContent!).Function, Is.SameAs(scope.Plugins.Single().Single()));
        await registration.DisposeAsync();
        plugin.Dispose();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var weakOwner = new WeakReference(owner);
        owner.Unload();
        return new RetainedChat(weakOwner, scope, result, registration);
    }

    /// <summary>
    /// Creates the production manager with no MCP or runtime activity.
    /// </summary>
    private static ChatPluginManager CreateManager(IServiceProvider services)
    {
        return new ChatPluginManager(services, [], new Settings(services), Substitute.For<IRuntimeManager>(), NullLogger<ChatPluginManager>.Instance);
    }

    /// <summary>
    /// Discovery keeps the real plugin and re-evaluates its available methods for each new chat scope.
    /// Existing snapshots keep their original selection until the entire registration is retired.
    /// </summary>
    [AvaloniaTest]
    public async Task Snapshot_AvailableFunctionsChange_UsesLiveDiscoveryAndPreservesOldSelection()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = CreateManager(services);
        using var plugin = new PipelinePlugin();
        await using var registration = manager.RegisterChatPlugin(plugin);
        using var chat = new ChatContext();
        var first = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        plugin.IsLimited = true;
        var second = await manager.CreateScopeAsync(new CustomAssistant(), chat, null, CancellationToken.None);
        Assert.That(first.Plugins.Single().FunctionCount, Is.EqualTo(4));
        Assert.That(second.Plugins.Single().Single().Name, Is.EqualTo("Value"));
        Assert.That(first.Plugins.Single().Any(function => function.Name == "Echo"), Is.True);
    }

    /// <summary>
    /// Keeps the host objects that historically rooted plugin functions alive during collection.
    /// </summary>
    private sealed record RetainedChat(WeakReference Owner, IChatPluginScope Scope, FunctionResultContent Result, IAsyncDisposable Registration);

    /// <summary>
    /// Uses the real SK filter contract without introducing another invocation adapter.
    /// </summary>
    private sealed class CallbackFilter(Func<FunctionInvocationContext, Func<FunctionInvocationContext, Task>, Task> callback) : IFunctionInvocationFilter
    {
        /// <inheritdoc />
        public Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next) => callback(context, next);
    }

    /// <summary>
    /// Provides methods whose results and injected objects must retain their original identities.
    /// </summary>
    private sealed class PipelinePlugin : ModChatPlugin
    {
        /// <inheritdoc />
        public override bool IsDefaultEnabled => true;

        /// <inheritdoc />
        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Pipeline");

        /// <inheritdoc />
        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Pipeline tests");

        /// <summary>
        /// Gets the original payload, intentionally not representable as an equivalent JSON object.
        /// </summary>
        internal object Payload { get; } = new();

        /// <summary>
        /// Gets the exact exception thrown by the tool.
        /// </summary>
        internal ApplicationException Failure { get; } = new("original failure");

        /// <summary>
        /// Gets the original result metadata container.
        /// </summary>
        internal IReadOnlyDictionary<string, object?> ResultMetadata { get; } = new Dictionary<string, object?> { ["source"] = "original" };

        /// <summary>
        /// Gets the parameters observed by the method.
        /// </summary>
        internal string? Observed { get; private set; }

        /// <summary>
        /// Gets the injected kernel.
        /// </summary>
        internal Kernel? ObservedKernel { get; private set; }

        /// <summary>
        /// Gets the injected service.
        /// </summary>
        internal object? ObservedService { get; private set; }

        /// <summary>
        /// Gets whether early disposal reached the original iterator.
        /// </summary>
        internal bool IsStreamDisposed { get; private set; }

        /// <summary>
        /// Gets or sets whether new scopes should expose only the simple value method.
        /// </summary>
        internal bool IsLimited { get; set; }

        /// <summary>
        /// Registers the real methods through the usual built-in tool path.
        /// </summary>
        internal PipelinePlugin() : base("pipeline")
        {
            _functionsSource.Add(new NativeChatFunction(Echo, ChatFunctionPermissions.FileRead));
            _functionsSource.Add(new NativeChatFunction(Value, ChatFunctionPermissions.FileRead));
            _functionsSource.Add(new NativeChatFunction(Stream, ChatFunctionPermissions.FileRead));
            _functionsSource.Add(new NativeChatFunction(Fail, ChatFunctionPermissions.FileRead));
        }

        /// <inheritdoc />
        public override ValueTask<IReadOnlyList<ChatFunction>> GetAvailableFunctionsAsync(CancellationToken cancellationToken)
        {
            return IsLimited ?
                ValueTask.FromResult<IReadOnlyList<ChatFunction>>(GetChatFunctions().Where(function => function.KernelFunction.Name == "Value").ToArray()) :
                base.GetAvailableFunctionsAsync(cancellationToken);
        }

        /// <summary>
        /// Exercises special parameters and custom FunctionResult handling in SK.
        /// </summary>
        private FunctionResult Echo(
            KernelFunction function, Kernel kernel, SearchRequest request, [FromKernelServices] object service, int limit = 20)
        {
            Observed = $"{request.Location.Directory}:{string.Join(',', request.Tags)}:{limit}:{function.PluginName}:{function.Name}";
            ObservedKernel = kernel;
            ObservedService = service;
            return new FunctionResult(function, Payload, kernel.Culture, ResultMetadata);
        }

        /// <summary>
        /// Completes before the post-invocation filter blocks.
        /// </summary>
        private string Value() => "value";

        /// <summary>
        /// Verifies the proxy does not replace tool failures with text-only exceptions.
        /// </summary>
        private string Fail() => throw Failure;

        /// <summary>
        /// Reports whether the original iterator was disposed before exhausting its values.
        /// </summary>
        private async IAsyncEnumerable<int> Stream()
        {
            try
            {
                yield return 1;
                await Task.Yield();
                yield return 2;
            }
            finally
            {
                IsStreamDisposed = true;
            }
        }
    }
}