using Everywhere.Plugin.Abstractions;
using Everywhere.Plugin.Hosting.Loading;

namespace Everywhere.Plugin.Hosting.Lifecycle;

/// <summary>
/// Owns one plugin instance and retains unfinished callbacks until they can safely complete.
/// </summary>
/// <remarks>
/// Instance disposal stops owned work before registered changes are reversed during hot unload.
/// Process exit joins the same instance-disposal operation and leaves registered changes in place.
/// </remarks>
internal sealed class PluginActivation(
    PluginDefinition definition,
    PluginContext context,
    AssemblyRegistry registry,
    string entryAssemblyName
)
{
    /// <summary>
    /// Gets the definition associated with this activation.
    /// </summary>
    internal PluginDefinition Definition { get; } = definition;

    /// <summary>
    /// Gets whether initialization completed and successful instance disposal has not yet completed.
    /// </summary>
    internal bool IsActive { get; private set; }

    /// <summary>
    /// Gets or sets whether execution is inside an engine-managed plugin callback, including async continuations.
    /// </summary>
    internal static bool IsInCallback
    {
        get => CallbackScope.Value;
        set => CallbackScope.Value = value;
    }

    /// <summary>
    /// Marks execution inside engine callbacks, including asynchronous continuations.
    /// </summary>
    private static readonly AsyncLocal<bool> CallbackScope = new();

    /// <summary>
    /// Holds the constructed entry, including when initialization completed with an error.
    /// </summary>
    private IPlugin? _plugin;

    /// <summary>
    /// Retains a callback that may still be executing after the host stopped waiting.
    /// </summary>
    private Task<string?>? _pending;

    /// <summary>
    /// Shares the instance-disposal result between hot retirement and process exit, without retrying
    /// a failed disposer or confusing its result with later registration cleanup.
    /// </summary>
    private Task<string?>? _disposalTask;

    /// <summary>
    /// Constructs and initializes the entry on a worker thread with contextual resolution.
    /// </summary>
    internal async Task<CallbackResult> InitializeAsync(TimeSpan timeout)
    {
        var result = await RunAsync(
            async () =>
            {
                var assembly = registry.GetAssembly(entryAssemblyName);
                var type = assembly.GetType(Definition.Entry.Type, throwOnError: true) ??
                    throw new InvalidOperationException("Entry type was not found.");

                if (!typeof(IPlugin).IsAssignableFrom(type) ||
                    type.IsAbstract ||
                    type.ContainsGenericParameters ||
                    !type.IsVisible ||
                    type.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new InvalidOperationException("The entry must be a public, concrete IPlugin with a public parameterless constructor.");
                }

                _plugin = (IPlugin?)Activator.CreateInstance(type) ??
                    throw new InvalidOperationException("The plugin constructor did not return an instance.");

                Directory.CreateDirectory(context.DataDirectory);
                await _plugin.InitializeAsync(context, context.Stopping);
            },
            timeout).ConfigureAwait(false);

        IsActive = result is { IsCompleted: true, Error: null };
        return result;
    }

    /// <summary>
    /// Signals stopping and awaits optional instance disposal before reversing registered changes.
    /// An incomplete callback prevents concurrent cleanup and leaves ownership retained for restart.
    /// </summary>
    internal async Task<CallbackResult> StopAsync(TimeSpan timeout)
    {
        if (_pending is not null)
        {
            return new CallbackResult(false, "A lifecycle operation is still running.");
        }

        _pending = _disposalTask ??= InvokeAsync(() => DisposeInstanceAsync(null));
        var result = await WaitForCallbackAsync(_pending, timeout).ConfigureAwait(false);

        if (!result.IsCompleted || result.Error is not null) return result;
        IsActive = false;

        result = await RunAsync(context.CleanupAsync, timeout).ConfigureAwait(false);

        if (result is { IsCompleted: true, Error: null })
        {
            _plugin = null;
        }

        return result;
    }

    /// <summary>
    /// Signals pending initialization to stop, then waits before disposing its instance. An existing
    /// disposal is joined instead of retried; registration cleanup is never started by process exit.
    /// </summary>
    internal async Task<CallbackResult> DisposeForExitAsync()
    {
        if (_disposalTask is null)
        {
            var initialization = _pending;
            _disposalTask = InvokeAsync(() => DisposeInstanceAsync(initialization));
        }
        else if (_pending is { } pending)
        {
            await pending.ConfigureAwait(false);
        }

        var error = await _disposalTask.ConfigureAwait(false);
        _pending = null;
        if (error is null) IsActive = false;

        return new CallbackResult(true, error);
    }

    /// <summary>
    /// Closes registration and signals stopping before disposing the entry. Like asynchronous MSDI
    /// disposal, prefer IAsyncDisposable and call exactly one interface; failure is not a reason to
    /// fall back to IDisposable. Pending initialization is canceled before it is joined, but entry
    /// disposal never overlaps it. A throwing cancellation callback must not skip entry disposal.
    /// </summary>
    private async Task DisposeInstanceAsync(Task<string?>? initialization)
    {
        try
        {
            context.BeginStopping();
        }
        finally
        {
            if (initialization is not null) await initialization.ConfigureAwait(false);

            switch (_plugin)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;

                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    /// <summary>
    /// Observes exceptions inside the worker and returns strings, preventing faulted tasks from retaining plugin exceptions.
    /// </summary>
    private Task<string?> InvokeAsync(Func<Task> callback)
    {
        return Task.Run(async () =>
        {
            CallbackScope.Value = true;

            try
            {
                using var scope = registry.EnterScope(entryAssemblyName);
                await callback();
                return null;
            }
            catch (Exception exception)
            {
                return exception.ToString();
            }
            finally
            {
                CallbackScope.Value = false;
            }
        });
    }

    /// <summary>
    /// Runs one callback with a bounded wait while retaining unfinished execution and ownership.
    /// </summary>
    private Task<CallbackResult> RunAsync(Func<Task> callback, TimeSpan timeout)
    {
        _pending = InvokeAsync(callback);
        return WaitForCallbackAsync(_pending, timeout);
    }

    /// <summary>
    /// Observes completion without aborting plugin code when the hot-unload waiting limit expires.
    /// </summary>
    private async Task<CallbackResult> WaitForCallbackAsync(Task<string?> pending, TimeSpan timeout)
    {
        try
        {
            var error = await pending.WaitAsync(timeout).ConfigureAwait(false);
            _pending = null;
            return new CallbackResult(true, error);
        }
        catch (TimeoutException)
        {
            return new CallbackResult(false, "The callback exceeded the host timeout; its execution was not aborted.");
        }
    }

    /// <summary>
    /// Separates observed completion from success without carrying an exception object across ownership boundaries.
    /// </summary>
    internal sealed record CallbackResult
    {
        /// <summary>
        /// Gets whether the callback has stopped executing.
        /// </summary>
        internal bool IsCompleted { get; }

        /// <summary>
        /// Gets diagnostic text when execution failed or timed out.
        /// </summary>
        internal string? Error { get; }

        /// <summary>
        /// Records one observed callback result.
        /// </summary>
        internal CallbackResult(bool isCompleted, string? error)
        {
            IsCompleted = isCompleted;
            Error = error;
        }
    }
}