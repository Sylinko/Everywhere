using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Lucide.Avalonia;

namespace Everywhere.Views;

[PseudoClasses(":expanded")]
public class ChatActionBubble : ContentControl
{
    /// <summary>
    /// Defines the <see cref="IsBusy"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<ChatActionBubble, bool>(nameof(IsBusy));

    /// <summary>
    /// Gets or sets a value indicating whether the action is currently in progress. Which displays a loading indicator.
    /// </summary>
    public bool IsBusy
    {
        get => GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="IsError"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsErrorProperty =
        AvaloniaProperty.Register<ChatActionBubble, bool>(nameof(IsError));

    /// <summary>
    /// Gets or sets a value indicating whether the action resulted in an error.
    /// Causing error content to be displayed when <see cref="IsBusy"/> is false.
    /// </summary>
    public bool IsError
    {
        get => GetValue(IsErrorProperty);
        set => SetValue(IsErrorProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="ErrorContent"/> property.
    /// </summary>
    public static readonly StyledProperty<object?> ErrorContentProperty =
        AvaloniaProperty.Register<ChatActionBubble, object?>(nameof(ErrorContent));

    /// <summary>
    /// Gets or sets the content to display when <see cref="IsError"/> is true and <see cref="IsBusy"/> is false.
    /// </summary>
    public object? ErrorContent
    {
        get => GetValue(ErrorContentProperty);
        set => SetValue(ErrorContentProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="ElapsedSeconds"/> property.
    /// </summary>
    public static readonly StyledProperty<double?> ElapsedSecondsProperty =
        AvaloniaProperty.Register<ChatActionBubble, double?>(nameof(ElapsedSeconds));

    /// <summary>
    /// Gets or sets the elapsed time in seconds since the action started.
    /// </summary>
    public double? ElapsedSeconds
    {
        get => GetValue(ElapsedSecondsProperty);
        set => SetValue(ElapsedSecondsProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="Header"/> property.
    /// </summary>
    public static readonly StyledProperty<object?> HeaderProperty =
        AvaloniaProperty.Register<ChatActionBubble, object?>(nameof(Header));

    /// <summary>
    /// Gets or sets the header content of the action bubble.
    /// </summary>
    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="Icon"/> property.
    /// </summary>
    public static readonly StyledProperty<LucideIconKind> IconProperty =
        AvaloniaProperty.Register<ChatActionBubble, LucideIconKind>(nameof(Icon));

    /// <summary>
    /// Gets or sets the icon to display in the action bubble header.
    /// </summary>
    public LucideIconKind Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="IsExpanded"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<ChatActionBubble, bool>(nameof(IsExpanded), true);

    /// <summary>
    /// Gets or sets a value indicating whether the action bubble is expanded to show its content (Including content and error content, if any).
    /// </summary>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="IsEffectivelyExpanded"/> property.
    /// </summary>
    public static readonly DirectProperty<ChatActionBubble, bool> IsEffectivelyExpandedProperty =
        AvaloniaProperty.RegisterDirect<ChatActionBubble, bool>(
            nameof(IsEffectivelyExpanded),
            o => o.IsEffectivelyExpanded);

    /// <summary>
    /// Gets a value indicating whether the action bubble is effectively expanded (i.e., <see cref="IsExpanded"/> is true and <see cref="ContentControl.Content"/> is not null).
    /// </summary>
    public bool IsEffectivelyExpanded => IsExpanded && Content is not null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Collapse when content is cleared
        if (change.Property == ContentProperty || change.Property == IsExpandedProperty)
        {
            var isEffectivelyExpanded = IsEffectivelyExpanded;
            RaisePropertyChanged(IsEffectivelyExpandedProperty, !isEffectivelyExpanded, isEffectivelyExpanded);
        }
        else if (change.Property == IsEffectivelyExpandedProperty)
        {
            PseudoClasses.Set(":expanded", change.NewValue is true);
        }
    }
}