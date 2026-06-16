using Everywhere.AI;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class SystemAssistantSettings : SettingsBase, ISettingsCategory
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