using System.Reactive.Disposables;
using DynamicData;

namespace Everywhere.Collections;

/// <summary>
/// Flattens an ordered list of observable child lists while preserving segment order for subsequent
/// child insertions. DynamicData's general-purpose <c>TransformMany</c> observes child lists, but it
/// does not provide the positional contract required by a virtualized UI when a child changes after
/// the initial flattening.
/// </summary>
/// <typeparam name="TSegment">The stable segment object type.</typeparam>
/// <typeparam name="TItem">The stable item object type.</typeparam>
public sealed class DynamicSegmentedList<TSegment, TItem> : IDisposable where TSegment : class where TItem : class
{
    /// <summary>
    /// Gets the bindable flat list. Its item references come directly from the child lists; the
    /// component never creates presentation objects or replaces equal-position stable references.
    /// </summary>
    public IReadOnlyBindableList<TItem> Items { get; }

    private readonly IObservableList<TSegment> _segments;
    private readonly Func<TSegment, IObservableList<TItem>> _itemsSelector;
    private readonly SourceList<TItem> _flattenedItems = new();
    private readonly IDisposable _itemsConnection;
    private readonly IDisposable _segmentsConnection;
    private CompositeDisposable _childConnections = new();
    private bool _isRewiring;
    private bool _isDisposed;

    public DynamicSegmentedList(IObservableList<TSegment> segments, Func<TSegment, IObservableList<TItem>> itemsSelector)
    {
        _segments = segments;
        _itemsSelector = itemsSelector;
        Items = _flattenedItems.Connect().BindEx(out _itemsConnection);
        _segmentsConnection = _segments.Connect().Subscribe(_ => RewireChildren());
    }

    /// <summary>
    /// Reconnects only when the top-level segment set changes. Child subscriptions then remain
    /// stable for ordinary activity/output updates within a turn.
    /// </summary>
    private void RewireChildren()
    {
        if (_isDisposed) return;

        _isRewiring = true;
        _childConnections.Dispose();
        _childConnections = new CompositeDisposable();

        foreach (var segment in _segments.Items)
        {
            _childConnections.Add(_itemsSelector(segment).Connect().Subscribe(_ => Synchronize()));
        }

        _isRewiring = false;
        Synchronize();
    }

    /// <summary>
    /// Applies the smallest contiguous remove/insert operation that can transform the current flat
    /// list into the concatenated child lists. Stable prefix and suffix references are untouched, so
    /// Avalonia keeps their containers and VariableHeightVirtualizingStackPanel measurements.
    /// </summary>
    private void Synchronize()
    {
        if (_isDisposed || _isRewiring) return;

        var desired = new List<TItem>();
        foreach (var segment in _segments.Items)
            desired.AddRange(_itemsSelector(segment).Items);

        _flattenedItems.Edit(current =>
        {
            var prefix = 0;
            while (prefix < current.Count && prefix < desired.Count && ReferenceEquals(current[prefix], desired[prefix]))
            {
                prefix++;
            }

            var suffix = 0;
            while (suffix < current.Count - prefix && suffix < desired.Count - prefix && ReferenceEquals(
                       current[current.Count - 1 - suffix],
                       desired[desired.Count - 1 - suffix]))
            {
                suffix++;
            }

            var removeCount = current.Count - prefix - suffix;
            if (removeCount > 0) current.RemoveRange(prefix, removeCount);

            var insertCount = desired.Count - prefix - suffix;
            if (insertCount > 0) current.InsertRange(desired.Skip(prefix).Take(insertCount), prefix);
        });
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        _segmentsConnection.Dispose();
        _childConnections.Dispose();
        _flattenedItems.Clear();
        _itemsConnection.Dispose();
        _flattenedItems.Dispose();
    }
}