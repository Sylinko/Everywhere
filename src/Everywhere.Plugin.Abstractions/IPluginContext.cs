namespace Everywhere.Plugin.Abstractions;

/// <summary>
/// Provides host services and registers reversible application changes for one plugin activation.
/// </summary>
public interface IPluginContext
{
    /// <summary>
    /// Gets the stable logical plugin identifier.
    /// </summary>
    string PluginId { get; }

    /// <summary>
    /// Gets host services. Contributions published into those services must be removed during hot unload.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Gets the cancellation token signaled before optional entry disposal. Stop accepting work when
    /// signaled, and await outstanding work in entry disposal before releasing the resources it uses.
    /// </summary>
    CancellationToken Stopping { get; }

    /// <summary>
    /// Gets the reason assigned before Stopping is signaled, or null while the activation is running.
    /// A disposal already started for unloading keeps its original reason when exit joins it.
    /// </summary>
    PluginStopReason? StopReason { get; }

    /// <summary>
    /// Gets activation-relative availability and host-owned information snapshots.
    /// </summary>
    IPluginCatalog Plugins { get; }

    /// <summary>
    /// Gets the final, read-only-by-contract root for installed resources.
    /// </summary>
    string InstallationDirectory { get; }

    /// <summary>
    /// Gets the writable data directory shared across installations of this plugin ID.
    /// </summary>
    string DataDirectory { get; }

    /// <summary>
    /// Registers a disposable handle whose disposal reverses a change made by this activation, such as
    /// a subscription, UI contribution or method patch. Hot unload and completed initialization-failure
    /// cleanup dispose accepted handles; ordinary process exit does not execute this reversal queue.
    /// </summary>
    /// <remarks>
    /// The resource must implement IDisposable or IAsyncDisposable; asynchronous disposal is preferred
    /// when both are implemented. Register each responsibility once and do not separately dispose the
    /// same handle unless it supports repeated disposal. Registration closes when this activation stops;
    /// a rejected handle remains caller-owned. Static registrations belong in PluginResource instead.
    /// Files, buffered data, connections and worker tasks remain the plugin's normal ownership responsibility.
    /// Registering a resource here does not arrange its release when the application exits.
    /// Entry disposal precedes reversal; keep each handle and the state needed for reversal usable afterward.
    /// If both paths release the same underlying resource, coordinate that release within its single owner.
    /// </remarks>
    void Register(object resource);
}