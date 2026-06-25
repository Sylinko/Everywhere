using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Views;
using Lucide.Avalonia;
using ShadUI;
using ShadUI.Themes;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class DisplaySettings : SettingsBase, ISettingsCategory
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
    public SettingsControl<AccentColorSelector> AccentColorControl => new(new AccentColorSelector
    {
        [!AccentColorSelector.SelectedColorProperty] = CompiledBinding.Create(
            (DisplaySettings x) => x.AccentColor,
            source: this,
            mode: BindingMode.TwoWay,
            converter: SerializableColorValueConverters.ToColor)
    });

    /// <summary>
    /// Application font size.
    /// </summary>
    [DynamicLocaleKey(
        LocaleKey.DisplaySettings_FontSize_Header,
        LocaleKey.DisplaySettings_FontSize_Description)]
    [SettingsItem(Group = "_")]
    [SettingsIntegerItem(Min = -1, Max = 3, IsTextBoxVisible = false)]
    public int FontSize
    {
        get
        {
            return Dispatcher.UIThread.Invoke(GetFontSize);

            int GetFontSize()
            {
                if (Application.Current is not { } app) return 0;

                var fontSizeM = app.Resources["FontSizeM"] as double? ?? 14;
                return fontSizeM switch
                {
                    < 14 => -1,
                    15 => 1,
                    16 => 2,
                    > 16 => 3,
                    _ => 0,
                };
            }
        }
        set
        {
            Dispatcher.UIThread.Invoke(SetFontSize);
            OnPropertyChanged();

            void SetFontSize()
            {
                if (Application.Current is not { } app) return;

                // <system:Double x:Key="FontSizeXs">10</system:Double>
                // <system:Double x:Key="FontSizeS">12.8</system:Double>
                // <system:Double x:Key="FontSizeM">14</system:Double>
                // <system:Double x:Key="FontSizeL">16</system:Double>
                // <system:Double x:Key="FontSizeXl">20</system:Double>
                // <system:Double x:Key="FontSize2Xl">24</system:Double>
                // <system:Double x:Key="FontSize3Xl">30</system:Double>
                // <system:Double x:Key="FontSize4Xl">48</system:Double>

                // value: -1, 0(default), 1, 2, 3

                var fontSizeM = value switch
                {
                    -1 => 12d,
                    1 => 15d,
                    2 => 16d,
                    3 => 18d,
                    _ => 14d,
                };
                app.Resources["FontSizeXs"] = fontSizeM * 0.714;
                app.Resources["FontSizeS"] = fontSizeM * 0.914;
                app.Resources["FontSizeM"] = fontSizeM;
                app.Resources["FontSizeL"] = fontSizeM * 1.142;
                app.Resources["FontSizeXl"] = fontSizeM * 1.428;
                app.Resources["FontSize2Xl"] = fontSizeM * 1.714;
                app.Resources["FontSize3Xl"] = fontSizeM * 2.142;
                app.Resources["FontSize4Xl"] = fontSizeM * 3.428;

                var lineHeightM = fontSizeM / 2 * 3;
                app.Resources["LineHeightXs"] = lineHeightM * 0.714;
                app.Resources["LineHeightS"] = lineHeightM * 0.914;
                app.Resources["LineHeightM"] = lineHeightM;
                app.Resources["LineHeightL"] = lineHeightM * 1.142;
                app.Resources["LineHeightXl"] = lineHeightM * 1.428;
                app.Resources["LineHeight2Xl"] = lineHeightM * 1.714;
                app.Resources["LineHeight3Xl"] = lineHeightM * 2.142;
                app.Resources["LineHeight4Xl"] = lineHeightM * 3.428;
            }
        }
    }
}
