using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Everywhere.Modding.Abstractions;

namespace ModFixture.Resources;

/// <summary>
/// Verifies that assembly ownership exists before module initialization can acquire resources.
/// </summary>
internal static class ModuleRegistration
{
    /// <summary>
    /// Registers without accessing a mod context or materializing an entry instance.
    /// </summary>
    [SuppressMessage("Usage", "CA2255", Justification = "The fixture verifies module-initializer resource ownership before entry activation.")]
    [ModuleInitializer]
    internal static void Initialize()
    {
        ModResource.Register(typeof(ModuleRegistration).Assembly, new Registration("module"));
    }
}

/// <summary>
/// Registers assembly resources before an activation context exists; two mods may share this DLL.
/// </summary>
public sealed class Entry : IMod
{
    /// <summary>
    /// Exercises every static owner overload, including asynchronous-only resources.
    /// </summary>
    static Entry()
    {
        ModResource.Register<Entry>(new Registration("generic"));
        ModResource.Register(typeof(Entry), new Registration("type"));
        ModResource.Register(typeof(Entry).Assembly, new AsyncRegistration());
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
    {
        State.Log = (Action<string>?)context.Services.GetService(typeof(Action<string>));
        State.Mode = context.Services.GetService(typeof(string)) as string;
        State.Gate = context.Services.GetService(typeof(TaskCompletionSource)) as TaskCompletionSource;
        context.Register(new ActivationRegistration(context.ModId));
        State.Log?.Invoke("initialize:" + context.ModId);
        if (context.Services.GetService(typeof(Action<IServiceProvider, Type>)) is Action<IServiceProvider, Type> inspect)
        {
            inspect(context.Services, GetType());
        }


        if (State.Mode == "initialize-failure") throw new InvalidOperationException("Injected initialization failure.");

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Makes the activation/assembly cleanup order observable.
    /// </summary>
    private sealed class ActivationRegistration(string id) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => State.Log?.Invoke("activation:" + id);
    }
}

/// <summary>
/// Fails before any mod instance or context can be supplied.
/// </summary>
public sealed class FailedEntry : IMod
{
    /// <summary>
    /// Leaves an externally rooted registration that only the assembly container can release.
    /// </summary>
    static FailedEntry()
    {
        ModResource.Register<FailedEntry>(new Registration("failed-constructor"));
        throw new InvalidOperationException("Injected static constructor failure.");
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

}

/// <summary>
/// Holds observations only inside the collectible assembly.
/// </summary>
internal static class State
{
    /// <summary>
    /// Gets or sets the host's value-only observation callback.
    /// </summary>
    internal static Action<string>? Log { get; set; }

    /// <summary>
    /// Gets or sets the injected cleanup behavior.
    /// </summary>
    internal static string? Mode { get; set; }

    /// <summary>
    /// Gets or sets the deliberately incomplete asynchronous cleanup gate.
    /// </summary>
    internal static TaskCompletionSource? Gate { get; set; }
}

/// <summary>
/// Installs a real Default-owned event root; dropping the resource list alone cannot collect this DLL.
/// </summary>
internal sealed class Registration : IDisposable
{
    /// <summary>
    /// Identifies this registration in host observations.
    /// </summary>
    private readonly string _name;

    /// <summary>
    /// Acquires an event subscription with an instance callback in the collectible assembly.
    /// </summary>
    internal Registration(string name)
    {
        _name = name;
        AppDomain.CurrentDomain.ProcessExit += OnExit;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        State.Log?.Invoke("assembly:" + _name);

        if (State.Mode == "cleanup-failure" && _name == "type")
        {
            throw new InvalidOperationException("Injected assembly resource failure.");
        }
    }

    /// <summary>
    /// Keeps the subscribed instance reachable without performing process-exit work.
    /// </summary>
    private void OnExit(object? sender, EventArgs args) { }
}

/// <summary>
/// Allows the test to observe a stalled disposer without releasing earlier registrations concurrently.
/// </summary>
internal sealed class AsyncRegistration : IAsyncDisposable
{
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        State.Log?.Invoke("assembly:async-start");
        if (State.Mode == "cleanup-timeout" && State.Gate is { } gate) await gate.Task;

        State.Log?.Invoke("assembly:async-end");
    }
}