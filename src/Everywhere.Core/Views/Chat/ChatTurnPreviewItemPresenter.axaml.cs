using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// Selects the visual for a turn preview item while keeping layout and animation item-agnostic.
/// </summary>
public sealed partial class ChatTurnPreviewItemPresenter : UserControl
{
    /// <summary>
    /// Defines the preview item displayed by this presenter.
    /// </summary>
    public static readonly StyledProperty<ChatTurnNavigationIndex.PreviewItem?> ItemProperty =
        AvaloniaProperty.Register<ChatTurnPreviewItemPresenter, ChatTurnNavigationIndex.PreviewItem?>(nameof(Item));

    /// <summary>
    /// Gets or sets the preview item displayed by this presenter.
    /// </summary>
    public ChatTurnNavigationIndex.PreviewItem? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>
    /// Creates a polymorphic preview presenter.
    /// </summary>
    public ChatTurnPreviewItemPresenter() => InitializeComponent();
}