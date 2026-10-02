using System.Windows.Input;
using Avalonia.Controls.Primitives;

namespace Everywhere.Views;

/// <summary>Displays the effective approval mode and requests a selection from its owning ViewModel.</summary>
public sealed class ToolApprovalSelector : TemplatedControl
{
    /// <summary>Defines the available approval options.</summary>
    public static readonly StyledProperty<IReadOnlyList<ToolApprovalModeOption>?> ItemsSourceProperty =
        AvaloniaProperty.Register<ToolApprovalSelector, IReadOnlyList<ToolApprovalModeOption>?>(nameof(ItemsSource));

    /// <summary>Defines the saved next-turn selection shown in the menu.</summary>
    public static readonly StyledProperty<ToolApprovalModeOption?> SelectedItemProperty =
        AvaloniaProperty.Register<ToolApprovalSelector, ToolApprovalModeOption?>(nameof(SelectedItem));

    /// <summary>Defines the currently effective option shown on the button.</summary>
    public static readonly StyledProperty<ToolApprovalModeOption?> EffectiveItemProperty =
        AvaloniaProperty.Register<ToolApprovalSelector, ToolApprovalModeOption?>(nameof(EffectiveItem));

    /// <summary>Defines the command that confirms and commits a requested selection.</summary>
    public static readonly StyledProperty<ICommand?> SelectItemCommandProperty =
        AvaloniaProperty.Register<ToolApprovalSelector, ICommand?>(nameof(SelectItemCommand));

    /// <summary>Gets or sets the stable approval options.</summary>
    public IReadOnlyList<ToolApprovalModeOption>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Gets or sets the saved next-turn selection.</summary>
    public ToolApprovalModeOption? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>Gets or sets the effective option.</summary>
    public ToolApprovalModeOption? EffectiveItem
    {
        get => GetValue(EffectiveItemProperty);
        set => SetValue(EffectiveItemProperty, value);
    }

    /// <summary>Gets or sets the selection command supplied by the owning ViewModel.</summary>
    public ICommand? SelectItemCommand
    {
        get => GetValue(SelectItemCommandProperty);
        set => SetValue(SelectItemCommandProperty, value);
    }
}
