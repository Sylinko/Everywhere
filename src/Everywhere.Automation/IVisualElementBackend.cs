namespace Everywhere.Automation;

/// <summary>
/// Provides process-shared platform entry points for acquiring visual elements that have no existing element receiver.
/// </summary>
/// <remarks>
/// The supplied retention selects the destination <see cref="VisualContext" /> through <see cref="VisualElementRetention.Context" />.
/// Implementations may own shared native clients, but must not retain contexts, retentions, or elements after a call returns.
/// </remarks>
public interface IVisualElementBackend
{
    /// <summary>
    /// Locates and queries one platform element through the requested topological resolution.
    /// </summary>
    /// <param name="retention">The ownership batch that retains the returned canonical element.</param>
    /// <param name="locator">The source sampled when the query executes, or the absence of an anchor when <see cref="VisualElementLocator.Default" /> is used.</param>
    /// <param name="resolution">The topological result to resolve from the located source.</param>
    /// <param name="request">The bounded scalar query, or <see langword="null" /> to use <see cref="VisualElementQueryRequest.Default" />.</param>
    /// <returns>The resolved element and its bounded observation, or <see langword="null" /> when no matching element is available.</returns>
    VisualElementQueryResult? Query(
        VisualElementRetention retention,
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? request = null);
}