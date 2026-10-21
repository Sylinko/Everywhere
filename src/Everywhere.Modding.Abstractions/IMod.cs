namespace Everywhere.Modding.Abstractions;

/// <summary>
/// Initializes one mod instance. Constructors must not publish changes or start work.
/// </summary>
/// <remarks>
/// Implement IDisposable or IAsyncDisposable when the instance owns resources needing final cleanup.
/// The engine prefers asynchronous disposal and invokes only one interface, including after completed
/// initialization failure. Disposal must stop and await owned work before releasing its resources.
/// Hot unload subsequently reverses registered application changes; normal process exit skips reversal.
/// Reversal handles must remain usable after entry disposal. Exit disposal cannot require a running UI
/// dispatcher or new service resolution. Forced or system termination does not guarantee disposal.
/// </remarks>
public interface IMod
{
    /// <summary>
    /// Initializes the mod after its selected dependencies have initialized.
    /// Register disposable handles that reverse application changes, and retain ownership of ordinary
    /// resources and background tasks. Registration is not a process-exit disposal service.
    /// </summary>
    ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken);
}