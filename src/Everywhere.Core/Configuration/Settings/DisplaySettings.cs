using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;
using ShadUI.Themes;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class DisplaySettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider), ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 1;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.MonitorCog;

    [SettingsItemIgnore]
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_Display_Header);

    [SettingsItemIgnore]
    public IDynamicLocaleKey? DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_Display_Description);

    /// <summary>
    /// Gets or sets the current application language.
    /// </summary>
    /// <remarks>
    /// Warn that this may be "default", which stands for en-US.
    /// </remarks>
    /// <example>
    /// default, zh-hans, ru, de, ja, it, fr, es, ko, pt-br, zh-hant, zh-hant-hk
    /// </example>
    [DynamicLocaleKey(
        LocaleKey.DisplaySettings_Language_Header,
        LocaleKey.DisplaySettings_Language_Description)]
    [SettingsItem(Group = "_")]
    [TypeConverter(typeof(LocaleNameTypeConverter))]
    public LocaleName Language
    {
        get => LocaleManager.CurrentLocale;
        set
        {
            if (LocaleManager.CurrentLocale == value) return;
            LocaleManager.CurrentLocale = value;
            OnPropertyChanged();
        }
    }

    [DynamicLocaleKey(
        LocaleKey.DisplaySettings_Theme_Header,
        LocaleKey.DisplaySettings_Theme_Description)]
    [SettingsItem(Group = "_")]
    public ThemeMode Theme
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            App.ThemeManager.SwitchTheme(value);
        }
    }

    [SettingsItemIgnore]
    public SerializableColor? AccentColor
    {
        get => SystemAccentColors.ColorOverride;
        set
        {
            SystemAccentColors.ColorOverride = value;
            OnPropertyChanged();
        }
    }

    [DynamicLocaleKey(
        LocaleKey.DisplaySettings_AccentColor_Header,
        LocaleKey.DisplaySettings_AccentColor_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<AccentColorSelector> AccentColorControl => new(
        new AccentColorSelector
        {
            [!AccentColorSelector.SelectedColorProperty] = CompiledBinding.Create(
                (DisplaySettings x) => x.AccentColor,
                source: this,
                mode: BindingMode.TwoWay,
                converter: SerializableColorValueConverters.ToColor)
        });

    /// <summary>
    /// Gets or sets the primary UI font family name. A null value restores the resource-defined default.
    /// </summary>
    [SettingsItemIgnore]
    public string? FontFamily
    {
        get;
        set
        {
            value = value?.Trim();
            if (string.Equals(field, value, StringComparison.OrdinalIgnoreCase)) return;

            Dispatcher.UIThread.Invoke(() => AppearanceManager.Shared.FontFamily = value);

            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets the font family picker used by the generated settings page.
    /// </summary>
    [DynamicLocaleKey(
        LocaleKey.DisplaySettings_Font_Header,
        LocaleKey.DisplaySettings_Font_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<FontFamilyPicker> FontFamilyControl => new(x => new FontFamilyPicker(x.GetRequiredService<FontFamilyCatalog>())
    {
        [!FontFamilyPicker.SelectedFontFamilyNameProperty] = CompiledBinding.Create(
            (DisplaySettings settings) => settings.FontFamily,
            source: this,
            mode: BindingMode.TwoWay)
    });

    /// <summary>
    /// Application font size.
    /// </summary>
    [DynamicLocaleKey(
        LocaleKey.DisplaySettings_FontSize_Header,
        LocaleKey.DisplaySettings_FontSize_Description)]
    [SettingsIntegerItem(Min = -1, Max = 3, IsTextBoxVisible = false, Group = "_")]
    public int FontSize
    {
        get;
        set
        {
            if (field == value) return;

            Dispatcher.UIThread.Invoke(() => AppearanceManager.Shared.FontSize = value switch
            {
                -1 => 12d,
                1 => 15d,
                2 => 16d,
                3 => 18d,
                _ => 14d,
            });

            field = value;
            OnPropertyChanged();
        }
    }
}