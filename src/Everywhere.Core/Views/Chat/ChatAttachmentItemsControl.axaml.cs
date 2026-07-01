using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Chat;

namespace Everywhere.Views;

[TemplatePart("PART_ItemsControl", typeof(ItemsControl), IsRequired = true)]
public class ChatAttachmentItemsControl : TemplatedControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        ItemsControl.ItemsSourceProperty.AddOwner<ChatAttachmentItemsControl>();

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly StyledProperty<Modalities> SupportedModalitiesProperty =
        AvaloniaProperty.Register<ChatAttachmentItemsControl, Modalities>(nameof(SupportedModalities));

    public Modalities SupportedModalities
    {
        get => GetValue(SupportedModalitiesProperty);
        set => SetValue(SupportedModalitiesProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="RemoveCommand"/> property.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatAttachment>?> RemoveCommandProperty =
        AvaloniaProperty.Register<ChatAttachmentItemsControl, IRelayCommand<ChatAttachment>?>(
            nameof(RemoveCommand));

    /// <summary>
    /// Gets or sets the command to remove an attachment.
    /// </summary>
    public IRelayCommand<ChatAttachment>? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    private ItemsControl? _itemsControl;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _itemsControl = e.NameScope.Find<ItemsControl>("PART_ItemsControl");
    }

    public bool TryGetAttachmentCenterOnScreen(ChatAttachment attachment, out PixelPoint center)
    {
        center = default;

        var target = _itemsControl?.ContainerFromItem(attachment);
        if (target is null || target.Bounds.Width <= 0 || target.Bounds.Height <= 0) return false;

        var topLeft = target.PointToScreen(default);
        var bottomRight = target.PointToScreen(new Point(target.Bounds.Width, target.Bounds.Height));
        if (bottomRight.X <= topLeft.X || bottomRight.Y <= topLeft.Y) return false;

        center = new PixelPoint((topLeft.X + bottomRight.X) / 2, (topLeft.Y + bottomRight.Y) / 2);
        return true;
    }
}