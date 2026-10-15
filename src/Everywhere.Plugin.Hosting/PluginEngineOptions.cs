namespace Everywhere.Plugin.Hosting;

/// <summary>
/// Defines host policy for cooperative callbacks and bounded collection observation.
/// </summary>
public sealed record PluginEngineOptions
{
    /// <summary>
    /// Gets the time allowed for each initialization or hot-unload callback. A timeout never aborts its code.
    /// Normal process-exit disposal waits for completion without a separate timeout.
    /// </summary>
    public TimeSpan CallbackTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the maximum number of forced collection observations per replacement.
    /// </summary>
    public int CollectionAttempts { get; init; } = 10;

    /// <summary>
    /// Gets the pause between collection observations; finalizer execution is not a strict time bound.
    /// </summary>
    public TimeSpan CollectionDelay { get; init; } = TimeSpan.FromMilliseconds(50);
}