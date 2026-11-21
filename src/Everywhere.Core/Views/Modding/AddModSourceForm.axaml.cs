using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Modding;
using Everywhere.Modding.Hosting.Management;
using Everywhere.Modding.Packaging;

namespace Everywhere.Views.Modding;

/// <summary>
/// Collects and validates inputs for adding a custom HTTP or local-folder mod repository source
/// inside a stacked confirmation dialog.
/// </summary>
public sealed partial class AddModSourceForm : UserControl
{
    private const string BuiltInSourceId = "builtin";

    /// <summary>
    /// Gets or sets the repository index URL or local folder path.
    /// </summary>
    public string? UrlOrPath
    {
        get => GetValue(UrlOrPathProperty);
        set => SetValue(UrlOrPathProperty, value);
    }

    /// <summary>
    /// Gets or sets the optional custom source identifier.
    /// </summary>
    public string? CustomId
    {
        get => GetValue(CustomIdProperty);
        set => SetValue(CustomIdProperty, value);
    }

    /// <summary>
    /// Gets or sets optional comma- or semicolon-separated mirror URLs.
    /// </summary>
    public string? Mirrors
    {
        get => GetValue(MirrorsProperty);
        set => SetValue(MirrorsProperty, value);
    }

    /// <summary>
    /// Gets or sets the inline validation or fetch error shown inside the form.
    /// </summary>
    public string? ErrorMessage
    {
        get => GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    public static readonly StyledProperty<string?> UrlOrPathProperty =
        AvaloniaProperty.Register<AddModSourceForm, string?>(nameof(UrlOrPath));

    public static readonly StyledProperty<string?> CustomIdProperty =
        AvaloniaProperty.Register<AddModSourceForm, string?>(nameof(CustomId));

    public static readonly StyledProperty<string?> MirrorsProperty =
        AvaloniaProperty.Register<AddModSourceForm, string?>(nameof(Mirrors));

    public static readonly StyledProperty<string?> ErrorMessageProperty =
        AvaloniaProperty.Register<AddModSourceForm, string?>(nameof(ErrorMessage));

    /// <summary>
    /// Initializes a new <see cref="AddModSourceForm"/> instance.
    /// </summary>
    public AddModSourceForm()
    {
        DataContext = this;
        InitializeComponent();
    }

    /// <summary>
    /// Validates required URL/folder and optional identifier constraints, updating <see cref="ErrorMessage"/> on failure.
    /// </summary>
    public bool ValidateInputs()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(UrlOrPath))
        {
            ErrorMessage = LocaleKey.ModPage_SourcesDialog_Validation_UrlRequired.I18N();
            return false;
        }

        if (string.IsNullOrWhiteSpace(CustomId))
        {
            return true;
        }

        var normalizedId = CustomId.Trim().ToLowerInvariant();
        if (normalizedId is BuiltInSourceId or ModManager.OfficialSourceId or ModStateRecord.LocalZipSourceId or ModStateRecord.LocalDirectorySourceId ||
            !ModValidationRules.IsValidIdentifier(normalizedId))
        {
            ErrorMessage = LocaleKey.ModPage_SourcesDialog_Validation_InvalidId.I18N();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Splits optional raw mirror text into normalized distinct mirror URLs.
    /// </summary>
    public static string[]? ParseMirrors(string? rawMirrors)
    {
        if (string.IsNullOrWhiteSpace(rawMirrors)) return null;

        var split = rawMirrors
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .AsValueEnumerable()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return split.Length > 0 ? split : null;
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? App.StorageProvider;
        var folders = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = LocaleKey.ModPage_SourcesDialog_BrowseFolder_ToolTip.I18N(),
                AllowMultiple = false
            });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            UrlOrPath = path;
            ErrorMessage = null;
        }
    }
}
