using System.Collections;

namespace Everywhere.Automation;

/// <summary>
/// Represents an empty, stateless visual-element relation.
/// </summary>
/// <remarks>
/// The shared instance is safe because enumeration and disposal never mutate this type.
/// </remarks>
public sealed class EmptyVisualElementEnumerator : IVisualElementEnumerator
{
    /// <summary>
    /// Gets the shared empty Enumerator.
    /// </summary>
    public static EmptyVisualElementEnumerator Shared { get; } = new();

    /// <inheritdoc />
    public VisualElementQueryResult Current => throw new InvalidOperationException("The Enumerator has no current item.");

    object IEnumerator.Current => Current;

    /// <inheritdoc />
    public int Count => 0;

    /// <inheritdoc />
    public int Index => -1;

    /// <inheritdoc />
    public bool HasMore => false;

    private EmptyVisualElementEnumerator()
    {
    }

    /// <inheritdoc />
    public bool MoveNext() => false;

    /// <inheritdoc />
    public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
