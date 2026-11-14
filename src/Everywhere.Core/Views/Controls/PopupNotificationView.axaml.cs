using Avalonia.Controls;
using Avalonia.Interactivity;
using Everywhere.Common.Notification;

namespace Everywhere.Views;

/// <summary>
/// Presents local content inside a common, explicitly acknowledged announcement shell.
/// </summary>
public sealed partial class PopupNotificationView : UserControl
{
    /// <summary>
    /// Occurs only when the user presses the acknowledgment button.
    /// </summary>
    public event EventHandler? Acknowledged;

    /// <summary>
    /// Creates the shell and its lazily requested local content.
    /// </summary>
    public PopupNotificationView(PopupNotification notification)
    {
        InitializeComponent();
        DataContext = notification;
    }

    private void HandleAcknowledgeClicked(object? sender, RoutedEventArgs e)
    {
        Acknowledged?.Invoke(this, EventArgs.Empty);
    }
}