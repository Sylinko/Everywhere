using System.Collections.Specialized;
using DynamicData;
using Everywhere.Collections;

namespace Everywhere.Core.Tests.Collections;

[TestFixture]
public class DynamicSegmentedListTests
{
    [Test]
    public void ChildInsert_UsesSegmentOffsetInFlatList()
    {
        using var first = new Segment("first-1", "first-2");
        using var second = new Segment("second-1");
        using var segments = new SourceList<Segment>();
        segments.AddRange([first, second]);
        using var flattened = new DynamicSegmentedList<Segment, string>(segments, segment => segment.Items);

        second.Source.Insert(0, "second-0");
        first.Source.Insert(1, "first-1.5");

        Assert.That(flattened.Items, Is.EqualTo(new[]
        {
            "first-1", "first-1.5", "first-2", "second-0", "second-1",
        }));
    }

    [Test]
    public void ChildInsert_DoesNotResetOrReplaceStablePrefixAndSuffix()
    {
        var firstItem = new object();
        var insertedItem = new object();
        var secondItem = new object();
        using var first = new Segment<object>(firstItem);
        using var second = new Segment<object>(secondItem);
        using var segments = new SourceList<Segment<object>>();
        segments.AddRange([first, second]);
        using var flattened = new DynamicSegmentedList<Segment<object>, object>(segments, segment => segment.Items);
        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)flattened.Items).CollectionChanged += (_, change) => actions.Add(change.Action);

        first.Source.Add(insertedItem);

        Assert.Multiple(() =>
        {
            Assert.That(flattened.Items, Is.EqualTo(new[] { firstItem, insertedItem, secondItem }));
            Assert.That(flattened.Items[0], Is.SameAs(firstItem));
            Assert.That(flattened.Items[2], Is.SameAs(secondItem));
            Assert.That(actions, Is.EqualTo(new[] { NotifyCollectionChangedAction.Add }));
        });
    }

    private sealed class Segment(params string[] items) : Segment<string>(items);

    private class Segment<T> : IDisposable where T : class
    {
        public SourceList<T> Source { get; } = new();
        public IObservableList<T> Items => Source;

        public Segment(params T[] items)
        {
            Source.AddRange(items);
        }

        public void Dispose() => Source.Dispose();
    }
}
