using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// Presents the terminal update command and the manual download link.
/// </summary>
public sealed partial class TerminalUpdateGuidance : UserControl
{
    /// <summary>
    /// Gets the exact command copied after the user confirms the dialog.
    /// </summary>
    public const string UpdateCommand = "ew update";

    /// <summary>
    /// Creates the compact update guidance content.
    /// </summary>
    public TerminalUpdateGuidance()
    {
        InitializeComponent();
    }
}
