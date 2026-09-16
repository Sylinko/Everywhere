using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Views;
using Lucide.Avalonia;
using Serilog;
using ShadUI;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class CommonSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider), ISettingsCategory
{
    [SettingsItemIgnore]
    private INativeHelper NativeHelper => GetRequiredService<INativeHelper>();

    [SettingsItemIgnore]
    public int Index => 0;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.Blocks;

    [SettingsItemIgnore]
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_Common_Header);

    [SettingsItemIgnore]
    public IDynamicLocaleKey? DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_Common_Description);

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial DateTimeOffset? LastUpdateCheckTime { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.SoftwareSettings_SoftwareUpdate_Header,
        LocaleKey.SoftwareSettings_SoftwareUpdate_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<SoftwareUpdateControl> SoftwareUpdate { get; } = new();

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.SoftwareSettings_IsAutomaticUpdateCheckEnabled_Header,
        LocaleKey.SoftwareSettings_IsAutomaticUpdateCheckEnabled_Description)]
    [SettingsItem(Group = "_")]
    public partial bool IsAutomaticUpdateCheckEnabled { get; set; } = true;

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.SoftwareSettings_IsStartupEnabled_Header,
        LocaleKey.SoftwareSettings_IsStartupEnabled_Description)]
    [SettingsItem(Group = "_")]
    public bool IsStartupEnabled
    {
        get => NativeHelper.IsStartupEnabled;
        set
        {
            try
            {
                NativeHelper.IsStartupEnabled = value;
                OnPropertyChanged();
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex); // maybe blocked by UAC or antivirus, handle it gracefully
                Log.ForContext<CommonSettings>().Error(ex, "Failed to set user startup enabled.");
                ToastManager.Error(LocaleResolver.Common_Error, ex.GetFriendlyMessage());
            }
        }
    }

    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.Empty)]
    [SettingsItem(Group = LocaleKey.SoftwareSettings_HostsStatus_Header, Classes = ["FullWidth", "NoHeading"])]
    public SettingsControl<HostsStatusControl> HostsStatus { get; } = new();

#if WINDOWS
    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.HostsStatusControl_ServiceMode_Header,
        LocaleKey.HostsStatusControl_ServiceMode_Description)]
    [SettingsItem(Group = LocaleKey.SoftwareSettings_HostsStatus_Header)]
    public SettingsControl<HostsServiceModeControl> HostsServiceMode { get; } = new();
#endif

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.SoftwareSettings_UpdateChannel_Header,
        LocaleKey.SoftwareSettings_UpdateChannel_Description)]
    [SettingsItem(Group = LocaleKey.Common_Advanced)]
    public partial UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Unknown;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.SoftwareSettings_IsStatisticsEnabled_Header,
        LocaleKey.SoftwareSettings_IsStatisticsEnabled_Description)]
    [SettingsItem(Group = LocaleKey.Common_Advanced)]
    public partial bool IsStatisticsEnabled { get; set; } = true;

    [DynamicLocaleKey(
        LocaleKey.SoftwareSettings_DiagnosticData_Header,
        LocaleKey.SoftwareSettings_DiagnosticData_Description)]
    [SettingsItem(Group = LocaleKey.Common_Advanced)]
    public bool DiagnosticData
    {
        get => !Telemetry.SendOnlyNecessaryData;
        set
        {
            Telemetry.SendOnlyNecessaryData = !value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.SoftwareSettings_DebugFeatures_Header,
        LocaleKey.SoftwareSettings_DebugFeatures_Description)]
    [SettingsItem(Group = LocaleKey.Common_Advanced)]
    public SettingsControl<DebugFeaturesControl> DebugFeatures { get; } = new();
}
