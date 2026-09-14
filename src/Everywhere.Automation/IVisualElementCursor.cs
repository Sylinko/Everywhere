namespace Everywhere.Automation;

/// <summary>
/// Implements the low-level, platform-owned cursor behind a visual relation Enumerator.
/// </summary>
/// <remarks>
/// Implementations may throw platform exceptions while advancing. <see cref="VisualElement.CreateEnumerator" />
/// invokes the cursor lazily and converts recoverable provider exceptions into terminal enumeration results.
/// Reading <see cref="IEnumerator{T}.Current" />, <see cref="Count" />, or <see cref="Index" /> must not perform
/// platform work or revalidate live topology.
/// </remarks>
public interface IVisualElementCursor : IEnumerator<VisualElementQueryResult>
{
    /// <summary>
    /// Gets the logical element count when known without additional provider work, or negative one when unknown.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the zero-based index of the current element, or negative one when there is no current element.
    /// </summary>
    int Index { get; }
}