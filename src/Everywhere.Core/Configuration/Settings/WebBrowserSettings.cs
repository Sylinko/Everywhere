using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Views;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class WebBrowserSettings : ObservableObject
{
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.WebBrowserSettings_ShowBrowser_Header,
        LocaleKey.WebBrowserSettings_ShowBrowser_Description)]
    [SettingsItem(Group = LocaleKey.BuiltInChatPlugin_Web_WebExtract_Header)]
    public partial bool ShowBrowser { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.WebBrowserSettings_OpenBrowser_Header,
        LocaleKey.WebBrowserSettings_OpenBrowser_Description)]
    [SettingsItem(Group = LocaleKey.BuiltInChatPlugin_Web_WebExtract_Header)]
    public SettingsControl<OpenWebBrowserControl> OpenBrowser { get; } = new();
}