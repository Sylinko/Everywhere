using Avalonia.Controls;
using Everywhere.Common;
using Everywhere.Configuration;

namespace Everywhere.Views;

public sealed class SettingsControlPresenter : Decorator
{
    public static readonly StyledProperty<SettingsControlItem?> ItemProperty =
        AvaloniaProperty.Register<SettingsControlPresenter, SettingsControlItem?>(nameof(Item));

    public SettingsControlItem? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private bool _isAttached;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemProperty)
        {
            ApplyItem();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _isAttached = true;
        ApplyItem();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        Child = null;

        base.OnDetachedFromVisualTree(e);
    }

    private void ApplyItem()
    {
        Child = _isAttached && Item is { } item ? item.CreateControl(ServiceLocator.Resolve<IServiceProvider>()) : null;
    }
}
