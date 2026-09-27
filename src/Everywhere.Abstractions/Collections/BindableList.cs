using DynamicData.Binding;

namespace Everywhere.Collections;

/// <summary>
/// A bindable list that supports replacing its contents with a single reset notification.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class BindableList<T> : ObservableCollectionExtended<T>, IReadOnlyBindableList<T>
{
    /// <summary>
    /// Replaces the contents of the list and raises a single reset notification after the new items are available.
    /// </summary>
    /// <param name="items">The replacement items.</param>
    public void Reset(IEnumerable<T> items)
    {
        using var notificationSuspension = SuspendNotifications();
        Clear();
        AddRange(items);
    }
}
