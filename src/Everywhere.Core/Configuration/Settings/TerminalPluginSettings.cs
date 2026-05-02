using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using ShadUI;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class TerminalPluginSettings : ObservableObject
{
    [DynamicResourceKey(
        LocaleKey.TerminalPluginSettings_ShellPath_Header,
        LocaleKey.TerminalPluginSettings_ShellPath_Description)]
    [SettingsStringItem]
    [ObservableProperty]
    public partial string? ShellPath { get; set; }

    [DynamicResourceKey(
        LocaleKey.TerminalPluginSettings_ShellArgs_Header,
        LocaleKey.TerminalPluginSettings_ShellArgs_Description)]
    [SettingsStringItem]
    [ObservableProperty]
    public partial string? ShellArgs { get; set; }

    [DynamicResourceKey(
        LocaleKey.TerminalPluginSettings_AutoApprove_Header,
        LocaleKey.TerminalPluginSettings_AutoApprove_Description)]
    public bool AutoApprove
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            if (value)
            {
                Dispatcher.UIThread.PostOnDemand(() => ServiceLocator.Resolve<ToastManager>().CreateToast(LocaleResolver.Common_Warning)
                    .WithContent(LocaleResolver.TerminalPluginSettings_AutoApprove_WarningToast_Content)
                    .WithDurationSeconds(5d)
                    .ShowWarning());
            }
        }
    }
}