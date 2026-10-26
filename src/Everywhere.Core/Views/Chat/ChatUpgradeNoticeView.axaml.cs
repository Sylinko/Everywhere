using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// Explains the local storage upgrade and keeps moving the old database to the trash opt-in.
/// </summary>
public sealed partial class ChatUpgradeNoticeView : UserControl
{
    /// <summary>
    /// Defines the unchecked optional trash choice.
    /// </summary>
    public static readonly StyledProperty<bool> ShouldTrashLegacyDatabaseProperty =
        AvaloniaProperty.Register<ChatUpgradeNoticeView, bool>(nameof(ShouldTrashLegacyDatabase));

    /// <summary>
    /// Gets or sets whether the user explicitly chose to move the previous-version database to the trash.
    /// </summary>
    public bool ShouldTrashLegacyDatabase
    {
        get => GetValue(ShouldTrashLegacyDatabaseProperty);
        set => SetValue(ShouldTrashLegacyDatabaseProperty, value);
    }

    /// <summary>
    /// Creates the upgrade notice content.
    /// </summary>
    public ChatUpgradeNoticeView()
    {
        InitializeComponent();
    }
}