using Everywhere.Modding.Abstractions;

namespace Everywhere.Modding.Hosting.Lifecycle;

/// <summary>
/// Owns a cancellation signal and the change-reversal handles registered by one activation.
/// </summary>
internal sealed class ModContext(ModDefinition definition, IServiceProvider services, IModCatalog mods) : IModContext
{
    /// <inheritdoc />
    public string ModId => definition.Id;

    /// <inheritdoc />
    public IServiceProvider Services { get; } = services;

    /// <inheritdoc />
    public CancellationToken Stopping => _stopping.Token;

    /// <inheritdoc />
    public ModStopReason? StopReason { get; private set; }

    /// <inheritdoc />
    public IModCatalog Mods { get; } = mods;

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
    /// and from the mod's ordinary resource ownership. Process exit does not drain this container.
    /// </summary>
    private readonly ModResource _resources = new();

    /// <inheritdoc />
    public void Register(object resource) => _resources.Register(resource);

    /// <summary>
    /// Closes registration before cancellation callbacks run. Cancellation failures are reported by the caller.
    /// </summary>
    internal void BeginStopping(ModStopReason reason)
    {
        StopReason = reason;
        _resources.Close();
        _stopping.Cancel();
    }

    /// <summary>
    /// Reverses registered activation changes after its callbacks have stopped. The callback boundary converts
    /// aggregate exceptions to diagnostic strings so mod exception objects do not escape the owner.
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