using Everywhere.AI;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class SystemAssistantSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider), ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 4;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.Sparkles;

    [SettingsItemIgnore]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_SystemAssistant_Header);

    [SettingsItemIgnore]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_SystemAssistant_Description);

    [DynamicResourceKey(
        LocaleKey.SystemAssistantSettings_TitleGeneration_Header,
        LocaleKey.SystemAssistantSettings_TitleGeneration_Desription)]
    [SettingsItems(IsExpandableBindingPath = $"!{nameof(TitleGeneration)}.{nameof(SystemAssistant.AutoSelect)}")]
    [SettingsTemplatedItem]
    public SystemAssistant TitleGeneration { get; } = new(ModelSpecializations.TitleGeneration);

    [DynamicResourceKey(
        LocaleKey.SystemAssistantSettings_DefaultSubagent_Header,
        LocaleKey.SystemAssistantSettings_DefaultSubagent_Description)]
    [SettingsItems(IsExpandableBindingPath = $"!{nameof(DefaultSubagent)}.{nameof(SystemAssistant.AutoSelect)}")]
    [SettingsTemplatedItem]
    public SystemAssistant DefaultSubagent { get; } = new(ModelSpecializations.Default);

    [DynamicResourceKey(
        LocaleKey.SystemAssistantSettings_ImageUnderstanding_Header,
        LocaleKey.SystemAssistantSettings_ImageUnderstanding_Description)]
    [SettingsItems(IsExpandableBindingPath = $"!{nameof(ImageUnderstanding)}.{nameof(SystemAssistant.AutoSelect)}")]
    [SettingsTemplatedItem]
    public SystemAssistant ImageUnderstanding { get; } = new(ModelSpecializations.ImageUnderstanding);
}