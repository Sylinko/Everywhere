using Avalonia.Controls;

namespace Everywhere.Views.Modding;

/// <summary>
/// Presents cascading dependent warnings, orphan cleanup, and data-deletion choices before uninstalling a mod.
/// </summary>
public sealed partial class ModUninstallConfirmForm : UserControl
{
    /// <summary>
    /// Defines the <see cref="ShouldDeleteData"/> styled property.
    /// </summary>
    public static readonly StyledProperty<bool> ShouldDeleteDataProperty =
        AvaloniaProperty.Register<ModUninstallConfirmForm, bool>(nameof(ShouldDeleteData));

    /// <summary>
    /// Defines the <see cref="ShouldRemoveOrphans"/> styled property.
    /// </summary>
    public static readonly StyledProperty<bool> ShouldRemoveOrphansProperty =
        AvaloniaProperty.Register<ModUninstallConfirmForm, bool>(nameof(ShouldRemoveOrphans));

    /// <summary>
    /// Gets the precomputed dependent and orphan summary for the mod being uninstalled.
    /// </summary>
    public ModPageViewModel.ModUninstallPreview Preview { get; }

    /// <summary>
    /// Gets or sets whether the mod's persisted user data directory should be deleted.
    /// </summary>
    public bool ShouldDeleteData
    {
        get => GetValue(ShouldDeleteDataProperty);
        set => SetValue(ShouldDeleteDataProperty, value);
    }

    /// <summary>
    /// Gets or sets whether unused auto-installed dependencies should also be uninstalled.
    /// </summary>
    public bool ShouldRemoveOrphans
    {
        get => GetValue(ShouldRemoveOrphansProperty);
        set => SetValue(ShouldRemoveOrphansProperty, value);
    }

    /// <summary>
    /// Initializes a new <see cref="ModUninstallConfirmForm"/> for <paramref name="preview"/>.
    /// </summary>
    public ModUninstallConfirmForm(ModPageViewModel.ModUninstallPreview preview)
    {
        Preview = preview;
        ShouldRemoveOrphans = preview.HasOrphans;

        DataContext = this;
        InitializeComponent();
    }
}
