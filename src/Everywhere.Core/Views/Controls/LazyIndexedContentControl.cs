using System.Collections.Specialized;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.Metadata;

namespace Everywhere.Views;

/// <summary>
/// A ContentControl that supports lazy loading of its content based on the ItemIndex property.
/// When not visible or attached to the visual tree, the content can be unloaded to save resources.
/// </summary>
public class LazyIndexedContentControl : ContentControl
{
    /// <summary>
    /// Identifies the <see cref="ItemIndex"/> property.
    /// </summary>
    public static readonly StyledProperty<int> ItemIndexProperty =
        AvaloniaProperty.Register<LazyIndexedContentControl, int>(nameof(ItemIndex), -1);

    /// <summary>
    /// Gets or sets the index of the item displayed in this control.
    /// </summary>
    public int ItemIndex
    {
        get => GetValue(ItemIndexProperty);
        set => SetValue(ItemIndexProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="ContentDataBinding"/> property.
    /// </summary>
    public static readonly StyledProperty<object?> ContentDataBindingProperty =
        AvaloniaProperty.Register<LazyIndexedContentControl, object?>(nameof(ContentDataBinding));

    /// <summary>
    /// Gets or sets the data context for the content of this control.
    /// If not set, the control's own DataContext is used.
    /// </summary>
    public object? ContentDataBinding
    {
        get => GetValue(ContentDataBindingProperty);
        set => SetValue(ContentDataBindingProperty, value);
    }

    [Content]
    public IAvaloniaList<IControlTemplate?> ItemTemplates { get; }

    /// <summary>
    /// Initializes the static members of the <see cref="LazyIndexedContentControl"/> class.
    /// </summary>
    static LazyIndexedContentControl()
    {
        ItemIndexProperty.Changed.AddClassHandler<LazyIndexedContentControl>(HandleItemIndexChanged);
        ContentDataBindingProperty.Changed.AddClassHandler<LazyIndexedContentControl>(HandleContentDataContextChanged);
    }

    private static void HandleItemIndexChanged(LazyIndexedContentControl sender, AvaloniaPropertyChangedEventArgs args)
    {
        sender.UpdateContent();
    }

    private static void HandleContentDataContextChanged(LazyIndexedContentControl sender, AvaloniaPropertyChangedEventArgs args)
    {
        // If the content is already loaded, update its DataContext
        if (sender.Content is Control control) control.DataContext = args.NewValue ?? sender.DataContext;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LazyIndexedContentControl"/> class.
    /// </summary>
    public LazyIndexedContentControl()
    {
        ItemTemplates = new AvaloniaList<IControlTemplate?>();
        ItemTemplates.CollectionChanged += OnItemTemplatesChanged;
    }

    /// <summary>
    /// Called when the control is attached to a rooted logical tree.
    /// </summary>
    /// <param name="e">The event args.</param>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);

        UpdateContent();
    }

    /// <summary>
    /// Called when the control is detached from a rooted logical tree.
    /// </summary>
    /// <param name="e">The event args.</param>
    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);

        Content = null;
    }

    /// <summary>
    /// Handles changes to the Items collection.
    /// </summary>
    private void OnItemTemplatesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateContent();
    }

    /// <summary>
    /// Updates the content of the control based on the current ItemIndex.
    /// </summary>
    private void UpdateContent()
    {
        if (!this.To<ILogical>().IsAttachedToLogicalTree)
        {
            return;
        }

        var index = ItemIndex;
        if (index >= 0 && index < ItemTemplates.Count)
        {
            var control = ItemTemplates[index]?.Build(this)?.Result;
            control?.DataContext = ContentDataBinding ?? DataContext;
            Content = control;
        }
        else
        {
            Content = null;
        }
    }
}