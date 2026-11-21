using Avalonia.Controls;

namespace Everywhere.Views.Modding;

/// <summary>
/// Displays pending restart mod changes, work-preservation guidance, and collapsible technical diagnostics.
/// </summary>
public sealed partial class ModRestartDetailsDialog : UserControl
{
    /// <summary>
    /// Gets the pending restart entries and formatted diagnostic snapshot.
    /// </summary>
    public ModPageViewModel.ModRestartDetailsSnapshot Snapshot { get; }

    /// <summary>
    /// Initializes a new <see cref="ModRestartDetailsDialog"/> for <paramref name="snapshot"/>.
    /// </summary>
    public ModRestartDetailsDialog(ModPageViewModel.ModRestartDetailsSnapshot snapshot)
    {
        Snapshot = snapshot;
        DataContext = this;
        InitializeComponent();
    }
}
