using Avalonia.Controls;
using Everywhere.Configuration;

namespace Everywhere.Views;

public sealed class SettingsGroupPanel : ItemsControl
{
    private readonly Dictionary<SettingsItem, IDisposable> _visibilitySubscriptions = [];
    private SettingsItem? _firstVisibleItem;
    private SettingsItem? _lastVisibleItem;
    private bool _isSyncingVisibilitySubscriptions;

    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingsGroupPanel, string?>(nameof(Header));

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = null;
        return item is not SettingsItemControl;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return item switch
        {
            SettingsItem => new SettingsItemControl(),
            _ => base.CreateContainerForItemOverride(item, index, recycleKey)
        };
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is SettingsItemControl settingsItemControl && item is SettingsItem settingsItem)
        {
            settingsItemControl.SetGroupBoundaryState(false, false);

            settingsItemControl.Item = settingsItem;
            settingsItemControl.Classes.AddRange(settingsItem.Classes);
            settingsItemControl[!IsEnabledProperty] = settingsItem[!SettingsItem.IsEnabledProperty];
            settingsItemControl[!IsVisibleProperty] = settingsItem[!SettingsItem.IsVisibleProperty];
            settingsItemControl[!SettingsItemControl.IsExpandableProperty] = settingsItem[!SettingsItem.IsExpandableProperty];
            settingsItemControl[!Expander.IsExpandedProperty] = settingsItem[!SettingsItem.IsExpandedProperty]; // TODO: TwoWay
            UpdateVisibleBoundaryStates();
            return;
        }

        base.PrepareContainerForItemOverride(container, item, index);
    }

    protected override void ContainerForItemPreparedOverride(Control container, object? item, int index)
    {
        base.ContainerForItemPreparedOverride(container, item, index);
        UpdateVisibleBoundaryStates();
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is SettingsItemControl settingsItemControl)
        {
            settingsItemControl.SetGroupBoundaryState(false, false);
            settingsItemControl.Item = null;
        }

        base.ClearContainerForItemOverride(container);
        UpdateVisibleBoundaryStates();
    }

    protected override void ContainerIndexChangedOverride(Control container, int oldIndex, int newIndex)
    {
        base.ContainerIndexChangedOverride(container, oldIndex, newIndex);
        UpdateVisibleBoundaryStates();
    }

    private void UpdateVisibleBoundaryStates()
    {
        var items = ItemsView.OfType<SettingsItem>().ToArray();
        SyncVisibilitySubscriptions(items);

        var (firstVisibleItem, lastVisibleItem) = FindVisibleBoundaryItems(items);
        if (!ReferenceEquals(_firstVisibleItem, firstVisibleItem) ||
            !ReferenceEquals(_lastVisibleItem, lastVisibleItem))
        {
            ClearGroupBoundaryState(_firstVisibleItem);
            if (!ReferenceEquals(_lastVisibleItem, _firstVisibleItem))
            {
                ClearGroupBoundaryState(_lastVisibleItem);
            }

            _firstVisibleItem = firstVisibleItem;
            _lastVisibleItem = lastVisibleItem;
        }

        ApplyGroupBoundaryState(_firstVisibleItem, _lastVisibleItem);
    }

    private void SyncVisibilitySubscriptions(IReadOnlyCollection<SettingsItem> items)
    {
        var currentItems = items.ToHashSet();
        _isSyncingVisibilitySubscriptions = true;
        try
        {
            foreach (var item in items)
            {
                if (_visibilitySubscriptions.ContainsKey(item)) continue;

                _visibilitySubscriptions[item] = item.GetObservable(SettingsItem.IsVisibleProperty)
                    .Subscribe(_ =>
                    {
                        if (_isSyncingVisibilitySubscriptions) return;

                        UpdateVisibleBoundaryStates();
                    });
            }
        }
        finally
        {
            _isSyncingVisibilitySubscriptions = false;
        }

        foreach (var item in _visibilitySubscriptions.Keys.ToArray())
        {
            if (currentItems.Contains(item)) continue;

            _visibilitySubscriptions[item].Dispose();
            _visibilitySubscriptions.Remove(item);
        }
    }

    private static (SettingsItem? First, SettingsItem? Last) FindVisibleBoundaryItems(IEnumerable<SettingsItem> items)
    {
        SettingsItem? firstVisibleItem = null;
        SettingsItem? lastVisibleItem = null;

        foreach (var item in items)
        {
            if (!item.IsVisible) continue;

            firstVisibleItem ??= item;
            lastVisibleItem = item;
        }

        return (firstVisibleItem, lastVisibleItem);
    }

    private void ApplyGroupBoundaryState(SettingsItem? firstVisibleItem, SettingsItem? lastVisibleItem)
    {
        if (firstVisibleItem is null) return;

        if (ReferenceEquals(firstVisibleItem, lastVisibleItem))
        {
            SetGroupBoundaryState(firstVisibleItem, true, true);
            return;
        }

        SetGroupBoundaryState(firstVisibleItem, true, false);
        SetGroupBoundaryState(lastVisibleItem, false, true);
    }

    private void ClearGroupBoundaryState(SettingsItem? item)
    {
        SetGroupBoundaryState(item, false, false);
    }

    private void SetGroupBoundaryState(SettingsItem? item, bool isFirst, bool isLast)
    {
        if (item is null) return;

        var container = ContainerFromItem(item) as SettingsItemControl ??
            GetRealizedContainers()
                .OfType<SettingsItemControl>()
                .FirstOrDefault(c => ReferenceEquals(c.Item, item));

        container?.SetGroupBoundaryState(isFirst, isLast);
    }
}
