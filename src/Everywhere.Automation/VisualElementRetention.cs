using System.Diagnostics;

namespace Everywhere.Automation;

/// <summary>
/// Retains each distinct visual element once for one real ownership boundary such as an attachment, Snapshot, or Agent turn.
/// </summary>
/// <remarks>
/// Retention is independent from RPC execution. Disposing it releases its complete ownership batch; elements shared by another batch remain alive through their other retainers.
/// </remarks>
public sealed class VisualElementRetention : IDisposable
{
    /// <summary>
    /// Gets the visual context whose identity domain this retention belongs to.
    /// </summary>
    public VisualContext Context { get; }

    /// <summary>
    /// Gets the number of distinct elements owned by this retention.
    /// </summary>
    public int Count => _elements.Count;

    public bool IsDisposed { get; private set; }

    private readonly HashSet<VisualElement> _elements = new(ReferenceEqualityComparer.Instance);

    internal VisualElementRetention(VisualContext context) => Context = context;

    /// <summary>
    /// Retains an already canonicalized element once in this ownership batch.
    /// </summary>
    /// <param name="element">The element to retain.</param>
    public void Retain(VisualElement element)
    {
        Context.ValidateRetention(this);
        Context.ValidateRetainedElement(element);
        RetainCanonical(element);
    }

    /// <summary>
    /// Releases every element owned by this batch.
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Context.Release(this, _elements);
        _elements.Clear();
    }

    internal void RetainCanonical(VisualElement element)
    {
        Debug.Assert(!IsDisposed);
        Debug.Assert(ReferenceEquals(element.Context, Context));

        if (_elements.Add(element))
        {
            Context.Retain(element);
        }
    }
}