using Everywhere.AI;
using Lucide.Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class SystemAssistantSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider), ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 4;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.Sparkles;

    [SettingsItemIgnore]
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_SystemAssistant_Header);

    [SettingsItemIgnore]
    public IDynamicLocaleKey? DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_SystemAssistant_Description);

    [DynamicLocaleKey(
        LocaleKey.SystemAssistantSettings_TitleGeneration_Header,
        LocaleKey.SystemAssistantSettings_TitleGeneration_Desription)]
    [SettingsItems(IsExpandableBindingPath = $"!{nameof(TitleGeneration)}.{nameof(SystemAssistant.AutoSelect)}")]
    [SettingsTemplatedItem]
    public SystemAssistant TitleGeneration { get; } = new(ModelSpecializations.TitleGeneration);

    [DynamicLocaleKey(
        LocaleKey.SystemAssistantSettings_ToolApproval_Header,
        LocaleKey.SystemAssistantSettings_ToolApproval_Description)]
    [SettingsItem(Group = "_ToolApproval")]
    [SettingsItems(IsExpandableBindingPath = $"!{nameof(ToolApproval)}.{nameof(SystemAssistant.AutoSelect)}")]
    [SettingsTemplatedItem]
    public SystemAssistant ToolApproval { get; } = new(ModelSpecializations.ToolApproval);

    /// <summary>
    /// Gets or sets whether newly started approval reviews can read file contents.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.SystemAssistantSettings_AllowApprovalFileReads_Header,
        LocaleKey.SystemAssistantSettings_AllowApprovalFileReads_Description)]
    [SettingsItem(Group = "_ToolApproval", IsExperimental = true)]
    public partial bool AllowApprovalFileReads { get; set; }

    [DynamicLocaleKey(
        LocaleKey.SystemAssistantSettings_DefaultSubagent_Header,
        LocaleKey.SystemAssistantSettings_DefaultSubagent_Description)]
    [SettingsItems(IsExpandableBindingPath = $"!{nameof(DefaultSubagent)}.{nameof(SystemAssistant.AutoSelect)}")]
    [SettingsTemplatedItem]
    public SystemAssistant DefaultSubagent { get; } = new(ModelSpecializations.Default);

    [DynamicLocaleKey(
        LocaleKey.SystemAssistantSettings_ImageUnderstanding_Header,
        LocaleKey.SystemAssistantSettings_ImageUnderstanding_Description)]
    [SettingsItems(IsExpandableBindingPath = $"!{nameof(ImageUnderstanding)}.{nameof(SystemAssistant.AutoSelect)}")]
    [SettingsTemplatedItem]
    public SystemAssistant ImageUnderstanding { get; } = new(ModelSpecializations.ImageUnderstanding);
}