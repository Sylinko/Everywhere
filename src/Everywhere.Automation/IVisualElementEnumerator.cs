using System.Collections;

namespace Everywhere.Automation;

/// <summary>
/// Enumerates one visual relation as successful element observations or one terminal relation failure.
/// </summary>
/// <remarks>
/// The Enumerator is single-use. A recoverable provider failure is yielded once as a
/// <see cref="VisualElementEnumerationResult" /> and then ends the sequence. Lifetime, cancellation,
/// argument, and programming errors continue to escape from <see cref="IEnumerator.MoveNext" />.
/// </remarks>
public interface IVisualElementEnumerator : IEnumerator<VisualElementEnumerationResult>, IEnumerable<VisualElementEnumerationResult>
{
    /// <summary>
    /// Gets the logical element count when known without additional provider work, or negative one when unknown.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the zero-based index of the current successful element, or negative one when there is no current element.
    /// </summary>
    int Index { get; }

    /// <summary>
    /// Returns this single-use Enumerator for natural <see langword="foreach" /> consumption.
    /// </summary>
    new IVisualElementEnumerator GetEnumerator() => this;

    IEnumerator<VisualElementEnumerationResult> IEnumerable<VisualElementEnumerationResult>.GetEnumerator() => this;

    IEnumerator IEnumerable.GetEnumerator() => this;
}