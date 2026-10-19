using System.Reflection;
using System.Runtime.CompilerServices;
using Everywhere.Plugin.Abstractions;

namespace PluginFixture;

/// <summary>
/// Installs host callbacks so tests observe actual collectible plugin behavior and cleanup.
/// </summary>
public sealed class Entry : IPlugin, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Supplies observations and the cooperative stopping signal for this instance.
    /// </summary>
    private IPluginContext? _context;

    /// <summary>
    /// Observes the public stop reason at initialization, cancellation and entry disposal.
    /// </summary>
    private Action<PluginStopReason?>? _observeStopReason;

    /// <summary>
    /// Holds real IO shared by ordinary disposal and a feature's reversal handle.
    /// </summary>
    private StreamWriter? _writer;

    /// <summary>
    /// Retains the writer's user until stopping has been observed and its final write has completed.
    /// </summary>
    private Task? _worker;

    /// <inheritdoc />
    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        var log = (Action<string>?)context.Services.GetService(typeof(Action<string>))
            ?? throw new InvalidOperationException("Missing fixture log.");
        log("initialize:" + context.PluginId);
        _observeStopReason = context.Services.GetService(typeof(Action<PluginStopReason?>)) as Action<PluginStopReason?>;
        if (_observeStopReason is { } observeStopReason)
        {
            observeStopReason(context.StopReason);
            context.Stopping.Register(() => observeStopReason(context.StopReason));
        }

        if (context.Services.GetService(typeof(Action<IServiceProvider, Type>)) is Action<IServiceProvider, Type> inspect)
        {
            inspect(context.Services, GetType());
        }

        context.Register(new Resource(() => log("dispose:first:" + context.PluginId)));
        context.Register(new Resource(() => log("dispose:second:" + context.PluginId)));

#if FIXTURE_A
        var result = context.Plugins.IsAvailable("fixture.b") ? InvokeOptional() : 1;
        log("optional:" + result);
#endif

        var mode = GetMode(context);
        if (mode == "native-probe") log("native:" + NativeProbe.Query());

        if (mode is "owned-io" or "owned-io-failure")
        {
            _writer = new StreamWriter(Path.Combine(context.DataDirectory, "owned.txt"));
            _worker = WriteWhenStoppingAsync(_writer, context.Stopping);
            context.Register(new IoReversal(ReleaseWriterAsync, log));

            if (mode == "owned-io-failure") throw new InvalidOperationException("Failure after acquiring owned IO.");
        }

#if !FIXTURE_A
        if (mode == "resources")
        {
            var resources = new System.Resources.ResourceManager("PluginFixture.B.Strings", typeof(Entry).Assembly);
            log("resource:" + resources.GetString("Message", System.Globalization.CultureInfo.GetCultureInfo("fr-CA")));
            log("resource:" + resources.GetString("Message", System.Globalization.CultureInfo.GetCultureInfo("de")));
            resources.ReleaseAllResources();
        }
#endif

        if (mode == "cleanup-failure")
        {
            context.Register(new Resource(() => throw new InvalidOperationException("Injected disposal failure.")));
        }

        if (mode == "retained")
        {
            var retain = (Action<object>?)context.Services.GetService(typeof(Action<object>))
                ?? throw new InvalidOperationException("Missing retention callback.");
            retain(this);
        }

        if (mode == "initialize-failure") throw new InvalidOperationException("Injected initialization failure.");

        if (mode == "initialize-cancellation") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        if (mode == "initialize-timeout")
        {
            var gate = (TaskCompletionSource?)context.Services.GetService(typeof(TaskCompletionSource))
                ?? throw new InvalidOperationException("Missing timeout gate.");
            await gate.Task;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_context is not { } context) return;
        var log = (Action<string>?)context.Services.GetService(typeof(Action<string>))
            ?? throw new InvalidOperationException("Missing fixture log.");
        log("entry-dispose:" + context.PluginId);
        _observeStopReason?.Invoke(context.StopReason);

        if (!context.Stopping.IsCancellationRequested)
        {
            throw new InvalidOperationException("Disposal must follow the stopping signal.");
        }

        if (_worker is not null) await _worker;
        await ReleaseWriterAsync();

        var mode = GetMode(context);
        if (mode == "disposal-failure")
        {
            throw new InvalidOperationException("Injected entry disposal failure.");
        }

        if (mode == "disposal-timeout")
        {
            var gate = (TaskCompletionSource?)context.Services.GetService(typeof(TaskCompletionSource))
                ?? throw new InvalidOperationException("Missing timeout gate.");
            await gate.Task;
        }
    }

    /// <inheritdoc />
    public void Dispose() => throw new InvalidOperationException("The engine must prefer asynchronous entry disposal.");

    /// <summary>
    /// Keeps using owned IO until cancellation and writes before the disposer may close the file.
    /// </summary>
    private static async Task WriteWhenStoppingAsync(StreamWriter writer, CancellationToken stopping)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }

        await writer.WriteAsync("finished");
    }

    /// <summary>
    /// Joins two serial release paths under one owner; no concurrent release is needed in this fixture.
    /// </summary>
    private async ValueTask ReleaseWriterAsync()
    {
        var writer = _writer;
        _writer = null;
        if (writer is null) return;

        await writer.DisposeAsync();
        if (_context?.Services.GetService(typeof(Action<string>)) is Action<string> log) log("owned-release");
    }

    /// <summary>
    /// Selects per-plugin behavior when a test needs independent branches of the dependency graph.
    /// </summary>
    private static string? GetMode(IPluginContext context)
    {
        return context.Services.GetService(typeof(Func<string, string>)) is Func<string, string> mode
            ? mode(context.PluginId)
            : context.Services.GetService(typeof(string)) as string;
    }

#if FIXTURE_A
    /// <summary>
    /// Resolves optional methods and base types only after the explicit availability check.
    /// </summary>
    [RequiresPlugin("fixture.b")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InvokeOptional()
    {
        var assembly = Assembly.Load("PluginFixture.B");

        if (assembly != typeof(DependencyApi).Assembly)
        {
            throw new InvalidOperationException("Dynamic and static bindings selected different assemblies.");
        }

        return new OptionalDerived().Value + DependencyApi.Value;
    }

    /// <summary>
    /// Exercises conditional inheritance in the ordinary entry DLL.
    /// </summary>
    [RequiresPlugin("fixture.b")]
    private sealed class OptionalDerived : DependencyBase;
#endif

    /// <summary>
    /// Records reverse-order resource disposal.
    /// </summary>
    private sealed class Resource(Action dispose) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => dispose();
    }

    /// <summary>
    /// Represents removal of a feature whose session also needs release during ordinary process exit.
    /// </summary>
    private sealed class IoReversal(Func<ValueTask> release, Action<string> log) : IAsyncDisposable
    {
        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            log("owned-undo");
            await release();
        }
    }
}

#if !FIXTURE_A
/// <summary>
/// Exercises an entry that only contributes a reversible change and has no final resource cleanup.
/// </summary>
public class PassiveEntry : IPlugin
{
    /// <summary>
    /// Gets the host-owned observation callback without retaining a service provider for exit resolution.
    /// </summary>
    protected Action<string>? Log { get; private set; }

    /// <inheritdoc />
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        Log = context.Services.GetService(typeof(Action<string>)) as Action<string>;
        context.Register(new Registration(Log));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Observes reversal independently of optional instance disposal.
    /// </summary>
    private sealed class Registration(Action<string>? log) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => log?.Invoke("undo");
    }
}

/// <summary>
/// Exercises the synchronous-only entry disposal path.
/// </summary>
public sealed class SynchronousEntry : PassiveEntry, IDisposable
{
    /// <inheritdoc />
    public void Dispose() => Log?.Invoke("entry-sync");
}

/// <summary>
/// Supplies a public base type to the optional consumer.
/// </summary>
public class DependencyBase
{
    /// <summary>
    /// Gets an observable value proving the derived type executed.
    /// </summary>
    public int Value => 20;
}

/// <summary>
/// Supplies a directly called optional API.
/// </summary>
public static class DependencyApi
{
    /// <summary>
    /// Gets an observable result from the selected dependency.
    /// </summary>
    public static int Value => 22;
}
#endif

/// <summary>
/// Imports a package-local native filename without eagerly loading it for other fixture scenarios.
/// </summary>
internal static class NativeProbe
{
    /// <summary>
    /// Calls a harmless Windows version API against a nonexistent file.
    /// </summary>
    internal static uint Query() => GetFileVersionInfoSize("nonexistent-everywhere-fixture", out _);

    /// <summary>
    /// Uses a private filename so the test cannot accidentally bind the ordinary system module by name.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("fixture_native", EntryPoint = "GetFileVersionInfoSizeW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetFileVersionInfoSize(string path, out uint handle);
}
