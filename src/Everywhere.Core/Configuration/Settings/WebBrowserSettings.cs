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
    public partial bool ShowBrowser { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.WebBrowserSettings_OpenBrowser_Header,
        LocaleKey.WebBrowserSettings_OpenBrowser_Description)]
    public SettingsControl<OpenWebBrowserControl> OpenBrowser { get; } = new();
}