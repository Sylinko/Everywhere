using Everywhere.Plugin.Abstractions;

namespace Everywhere.Plugin.Hosting.Lifecycle;

/// <summary>
/// Owns a cancellation signal and the change-reversal handles registered by one activation.
/// </summary>
internal sealed class PluginContext(PluginDefinition definition, IServiceProvider services, IPluginCatalog plugins) : IPluginContext
{
    /// <inheritdoc />
    public string PluginId => definition.Id;

    /// <inheritdoc />
    public IServiceProvider Services { get; } = services;

    /// <inheritdoc />
    public CancellationToken Stopping => _stopping.Token;

    /// <inheritdoc />
    public IPluginCatalog Plugins { get; } = plugins;

    /// <inheritdoc />
    public string InstallationDirectory => definition.Installation.Directory;

    /// <inheritdoc />
    public string DataDirectory => definition.DataDirectory;

    /// <summary>
    /// Signals cooperative shutdown independently of resource disposal.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Holds this activation's change-reversal handles, separate from assembly registrations
    /// and from the plugin's ordinary resource ownership. Process exit does not drain this container.
    /// </summary>
    private readonly PluginResource _resources = new();

    /// <inheritdoc />
    public void Register(object resource) => _resources.Register(resource);

    /// <summary>
    /// Closes registration before cancellation callbacks run. Cancellation failures are reported by the caller.
    /// </summary>
    internal void BeginStopping()
    {
        _resources.Close();
        _stopping.Cancel();
    }

    /// <summary>
    /// Reverses registered activation changes after its callbacks have stopped. The callback boundary converts
    /// aggregate exceptions to diagnostic strings so plugin exception objects do not escape the owner.
    /// </summary>
    internal async Task CleanupAsync()
    {
        try
        {
            await _resources.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _stopping.Dispose();
        }
    }
}
