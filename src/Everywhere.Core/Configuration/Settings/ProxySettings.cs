using CommunityToolkit.Mvvm.ComponentModel;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class ProxySettings : SettingsBase, ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 3;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.WifiCog;

    [SettingsItemIgnore]
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_Proxy_Header);

    [SettingsItemIgnore]
    public IDynamicLocaleKey? DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_Proxy_Description);

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.ProxySettings_IsEnabled_Header,
        LocaleKey.ProxySettings_IsEnabled_Description)]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.ProxySettings_Endpoint_Header,
        LocaleKey.ProxySettings_Endpoint_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsEnabled), Group = "_")]
    [SettingsStringItem(Watermark = "http://127.0.0.1:7890")]
    public partial string? Endpoint { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.ProxySettings_BypassOnLocal_Header,
        LocaleKey.ProxySettings_BypassOnLocal_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsEnabled), Group = "_")]
    public partial bool BypassOnLocal { get; set; } = true;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.ProxySettings_BypassList_Header,
        LocaleKey.ProxySettings_BypassList_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsEnabled), Group = "_")]
    [SettingsStringItem(Watermark = "www.example.com", IsMultiline = true, Height = 96)]
    public partial string? BypassList { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.ProxySettings_UseAuthentication_Header,
        LocaleKey.ProxySettings_UseAuthentication_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsEnabled), Group = "_")]
    public partial bool UseAuthentication { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.ProxySettings_Username_Header,
        LocaleKey.ProxySettings_Username_Description)]
    [SettingsItem(IsVisibleBindingPath = $"{nameof(IsEnabled)} && {nameof(UseAuthentication)}", Group = "_")]
    public partial string? Username { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.ProxySettings_Password_Header,
        LocaleKey.ProxySettings_Password_Description)]
    [SettingsItem(IsVisibleBindingPath = $"{nameof(IsEnabled)} && {nameof(UseAuthentication)}", Group = "_")]
    [SettingsStringItem(IsPassword = true)]
    public partial string? Password { get; set; }
}