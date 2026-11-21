using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Modding;
using Everywhere.Modding.Hosting.Management;

namespace Everywhere.Views.Modding;

/// <summary>
/// Displays resolved mod packages, dependency activations, and license terms awaiting user acceptance
/// inside an installation or update confirmation dialog.
/// </summary>
public sealed partial class ModInstallConfirmForm : UserControl
{
    /// <summary>
    /// Defines the <see cref="LicenseViewerText"/> direct property.
    /// </summary>
    public static readonly DirectProperty<ModInstallConfirmForm, string?> LicenseViewerTextProperty =
        AvaloniaProperty.RegisterDirect<ModInstallConfirmForm, string?>(
            nameof(LicenseViewerText),
            owner => owner.LicenseViewerText);

    /// <summary>
    /// Defines the <see cref="IsLicenseReaderVisible"/> direct property.
    /// </summary>
    public static readonly DirectProperty<ModInstallConfirmForm, bool> IsLicenseReaderVisibleProperty =
        AvaloniaProperty.RegisterDirect<ModInstallConfirmForm, bool>(
            nameof(IsLicenseReaderVisible),
            owner => owner.IsLicenseReaderVisible);

    /// <summary>
    /// Gets the planned packages, dependency activations, and pending license summaries.
    /// </summary>
    public ModPageViewModel.ModInstallPlanPreview Preview { get; }

    /// <summary>
    /// Gets the primary dialog confirmation label matching whether licenses or downloads are included.
    /// </summary>
    public string PrimaryButtonText { get; }

    /// <summary>
    /// Gets the loaded inline license text shown below the pending license list.
    /// </summary>
    public string? LicenseViewerText
    {
        get;
        private set => SetAndRaise(LicenseViewerTextProperty, ref field, value);
    }

    /// <summary>
    /// Gets whether the inline license reader panel is visible.
    /// </summary>
    public bool IsLicenseReaderVisible
    {
        get;
        private set => SetAndRaise(IsLicenseReaderVisibleProperty, ref field, value);
    }

    private readonly IModManager _modManager;

    /// <summary>
    /// Initializes a new <see cref="ModInstallConfirmForm"/> for <paramref name="preview"/>.
    /// </summary>
    public ModInstallConfirmForm(ModPageViewModel.ModInstallPlanPreview preview, IModManager modManager)
    {
        Preview = preview;
        _modManager = modManager;
        PrimaryButtonText = preview.HasLicenses
            ? LocaleKey.ModPage_ConfirmInstall_AcceptButton.I18N()
            : preview.HasPackages
                ? LocaleKey.ModPage_InstallButton_Content.I18N()
                : LocaleKey.Common_Yes.I18N();

        DataContext = this;
        InitializeComponent();
    }

    /// <summary>
    /// Builds accepted license timestamps for all pending licenses in <see cref="Preview"/>.
    /// </summary>
    public IReadOnlyDictionary<string, ModAcceptedLicense> BuildAcceptedLicenses()
    {
        var acceptedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var accepted = new Dictionary<string, ModAcceptedLicense>(StringComparer.Ordinal);
        foreach (var entry in Preview.PendingLicenses)
        {
            accepted[entry.ModId] = new ModAcceptedLicense
            {
                Spdx = entry.Spdx,
                AcceptedAtUnixSeconds = acceptedAt
            };
        }

        return accepted;
    }

    [RelayCommand]
    private async Task ViewLicenseAsync(ModPageViewModel.ModInstallLicenseEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is not { DocumentReference: { Length: > 0 } docRef }) return;

        try
        {
            var text = await _modManager.ReadDocumentAsync(entry.SourceId, docRef, cancellationToken);
            LicenseViewerText = string.IsNullOrWhiteSpace(text)
                ? LocaleKey.ModPage_LicenseDialog_Empty.I18N()
                : text;
            IsLicenseReaderVisible = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LicenseViewerText = exception.Message;
            IsLicenseReaderVisible = true;
        }
    }
}
