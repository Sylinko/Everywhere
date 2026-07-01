using System.Collections.Specialized;
using Avalonia.Controls;
using Everywhere.Chat.Plugins;

namespace Everywhere.Views;

public sealed class ChatPluginUserInterfaceHost : ContentControl
{
    public static readonly StyledProperty<IList<ChatPluginUserInterfaceItem>?> ItemsSourceProperty =
        AvaloniaProperty.Register<ChatPluginUserInterfaceHost, IList<ChatPluginUserInterfaceItem>?>(nameof(ItemsSource));

    public IList<ChatPluginUserInterfaceItem>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsSourceProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= HandleItemsSourceChanged;
            }

            if (change.NewValue is INotifyCollectionChanged newCollection)
            {
                newCollection.CollectionChanged += HandleItemsSourceChanged;
            }

            HandleItemsSourceChanged();
        }
    }

    private void HandleItemsSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HandleItemsSourceChanged();
    }

    private void HandleItemsSourceChanged()
    {
        Content = ItemsSource?.FirstOrDefault();
    }
}