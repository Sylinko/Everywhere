using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Mods.Themes.Controls;

namespace Everywhere.Mods.Themes;

/// <summary>
/// Stores persisted theme selections and builds settings controls for the Themes mod.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class ThemeSettings : ObservableObject
{
    private readonly string _customThemesDirectory;
    private readonly Action _reloadCustomThemes;

    /// <summary>
    /// Initializes settings with defaults; used by source-generated settings deserialization.
    /// </summary>
    public ThemeSettings() : this(string.Empty, static () => { }) { }

    /// <summary>
    /// Initializes settings bound to the custom JSON directory and reload callback owned by <see cref="ThemesMod"/>.
    /// </summary>
    internal ThemeSettings(string customThemesDirectory, Action reloadCustomThemes)
    {
        _customThemesDirectory = customThemesDirectory;
        _reloadCustomThemes = reloadCustomThemes;
    }

    /// <summary>
    /// Gets or sets the theme identifier applied when the application runs in light mode.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string LightThemeId { get; set; } = BuiltInThemes.DefaultLightId;

    /// <summary>
    /// Gets or sets the theme identifier applied when the application runs in dark mode.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string DarkThemeId { get; set; } = BuiltInThemes.DefaultDarkId;

    /// <summary>
    /// Presents the light-mode theme selector in settings.
    /// </summary>
    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.Themes_LightTheme_Header,
        LocaleKey.Themes_LightTheme_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ThemeSelectorControl> LightThemeControl => new(
        () => new ThemeSelectorControl
        {
            Variant = ThemeBaseVariant.Light,
            [!ThemeSelectorControl.SelectedThemeIdProperty] = CompiledBinding.Create(
                static (ThemeSettings x) => x.LightThemeId,
                source: this,
                mode: BindingMode.TwoWay)
        },
        isCacheEnabled: false);

    /// <summary>
    /// Presents the dark-mode theme selector in settings.
    /// </summary>
    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.Themes_DarkTheme_Header,
        LocaleKey.Themes_DarkTheme_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ThemeSelectorControl> DarkThemeControl => new(
        () => new ThemeSelectorControl
        {
            Variant = ThemeBaseVariant.Dark,
            [!ThemeSelectorControl.SelectedThemeIdProperty] = CompiledBinding.Create(
                static (ThemeSettings x) => x.DarkThemeId,
                source: this,
                mode: BindingMode.TwoWay)
        },
        isCacheEnabled: false);

    /// <summary>
    /// Gets or sets whether the global Display Settings accent color overrides the selected theme's primary color.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.Themes_ShouldOverrideAccentColor_Header,
        LocaleKey.Themes_ShouldOverrideAccentColor_Description)]
    [SettingsItem(Group = "_")]
    public partial bool ShouldOverrideAccentColor { get; set; }

    /// <summary>
    /// Presents actions for managing user-provided JSON theme files.
    /// </summary>
    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.Themes_CustomThemes_Header,
        LocaleKey.Themes_CustomThemes_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<CustomThemesActionControl> CustomThemesControl => new(
        () => new CustomThemesActionControl(_customThemesDirectory, _reloadCustomThemes),
        isCacheEnabled: false);
}

/// <summary>
/// Provides explicit settings metadata owned by this mod activation.
/// </summary>
[GeneratedSettingsDescriptor(typeof(ThemeSettings))]
public partial class ThemeSettingsDescriptors;

/// <summary>
/// Keeps JSON metadata out of the host's global serializer configuration.
/// </summary>
[JsonSerializable(typeof(ThemeSettings))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
internal partial class ThemeSettingsJsonSerializerContext : JsonSerializerContext;
