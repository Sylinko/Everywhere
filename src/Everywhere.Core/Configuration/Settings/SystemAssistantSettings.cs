using Everywhere.AI;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class SystemAssistantSettings : SettingsBase, ISettingsCategory
{
    [HiddenSettingsItem]
    public int Index => 4;

    [HiddenSettingsItem]
    public LucideIconKind Icon => LucideIconKind.Sparkles;

    [HiddenSettingsItem]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_SystemAssistant_Header);

    [HiddenSettingsItem]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_SystemAssistant_Description);

    [DynamicResourceKey(
        LocaleKey.SystemAssistantSettings_TitleGeneration_Header,
        LocaleKey.SystemAssistantSettings_CommonDesription)]
    [SettingsItems(IsExpandableBindingPath = $"!{nameof(TitleGeneration)}.{nameof(SystemAssistant.AutoSelect)}")]
    [SettingsTemplatedItem]
    public SystemAssistant TitleGeneration { get; } = new(ModelSpecializations.TitleGeneration);
}