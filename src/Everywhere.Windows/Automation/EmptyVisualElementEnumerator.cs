using System.Collections;
using Everywhere.Automation;

namespace Everywhere.Windows.Automation;

internal sealed class EmptyVisualElementEnumerator : IVisualElementEnumerator
{
    public VisualElementQueryResult Current => throw new InvalidOperationException("The Enumerator has no current item.");

    object IEnumerator.Current => Current;

    public int Count => 0;

    public int Index => -1;

    public bool HasMore => false;

    public bool MoveNext() => false;

    public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

    public void Dispose()
    {
    }
}