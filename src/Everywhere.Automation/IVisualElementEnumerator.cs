namespace Everywhere.Automation;

/// <summary>
/// Extends the standard enumerator contract with optional bounded-observation metadata and non-consuming lookahead.
/// </summary>
public interface IVisualElementEnumerator : IEnumerator<VisualElementQueryResult>
{
    /// <summary>
    /// Gets the logical item count when known without provider work, or negative one when unknown.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the zero-based index of the current item, or negative one when there is no current item.
    /// </summary>
    int Index { get; }

    /// <summary>
    /// Gets whether another item is available without changing <see cref="IEnumerator{T}.Current"/>.
    /// </summary>
    bool HasMore { get; }
}