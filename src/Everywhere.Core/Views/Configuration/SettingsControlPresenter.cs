using Avalonia.Controls;
using Everywhere.Common;
using Everywhere.Configuration;

namespace Everywhere.Views;

public sealed class SettingsControlPresenter : Control
{
    public static readonly StyledProperty<SettingsControlItem?> ItemProperty =
        AvaloniaProperty.Register<SettingsControlPresenter, SettingsControlItem?>(nameof(Item));

    public SettingsControlItem? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private Control? _content;
    private bool _isAttached;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemProperty)
        {
            _content = change.NewValue is SettingsControlItem item ?
                item.CreateControl(ServiceLocator.Resolve<IServiceProvider>()) :
                null;
            ApplyContent();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _isAttached = true;
        ApplyContent();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _isAttached = false;
        _content = null;
        ApplyContent();
    }

    private void ApplyContent()
    {
        while (LogicalChildren.Count > 0) LogicalChildren.RemoveAt(LogicalChildren.Count - 1);
        if (_isAttached && _content != null) LogicalChildren.Add(_content);
    }
}
