using Avalonia.Controls;

namespace Everywhere.Views.Modding;

/// <summary>
/// Displays a scrollable license document and bundled third-party notices for a mod.
/// </summary>
public sealed partial class ModLicenseDialogView : UserControl
{
    /// <summary>
    /// Gets the combined license text, if available.
    /// </summary>
    public string? LicenseText { get; }

    /// <summary>
    /// Gets whether non-empty license text is available to display.
    /// </summary>
    public bool HasLicenseText => !string.IsNullOrWhiteSpace(LicenseText);

    /// <summary>
    /// Initializes a new <see cref="ModLicenseDialogView"/> displaying <paramref name="licenseText"/>.
    /// </summary>
    public ModLicenseDialogView(string? licenseText)
    {
        LicenseText = licenseText;
        DataContext = this;
        InitializeComponent();
    }
}
