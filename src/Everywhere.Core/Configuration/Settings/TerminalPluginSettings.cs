using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class TerminalPluginSettings : ObservableObject
{
    [DynamicLocaleKey(
        LocaleKey.TerminalPluginSettings_ShellPath_Header,
        LocaleKey.TerminalPluginSettings_ShellPath_Description)]
    [SettingsStringItem]
    [ObservableProperty]
    public partial string? ShellPath { get; set; }
}