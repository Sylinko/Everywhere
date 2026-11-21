using Avalonia.Controls;

namespace Everywhere.Views.Modding;

/// <summary>
/// Displays structured dependency resolution errors without plain-text bullet lists.
/// </summary>
public sealed partial class ModDependencyErrorDialog : UserControl
{
    /// <summary>
    /// Gets the structured dependency failure preview shown in the dialog body.
    /// </summary>
    public ModPageViewModel.ModDependencyFailurePreview Preview { get; }

    /// <summary>
    /// Initializes a new <see cref="ModDependencyErrorDialog"/> for <paramref name="preview"/>.
    /// </summary>
    public ModDependencyErrorDialog(ModPageViewModel.ModDependencyFailurePreview preview)
    {
        Preview = preview;
        DataContext = this;
        InitializeComponent();
    }
}
