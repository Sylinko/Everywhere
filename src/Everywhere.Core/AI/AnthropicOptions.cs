using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

public enum AnthropicRequestThinkingConfig
{
    [DynamicLocaleKey(LocaleKey.AnthropicRequestThinkingConfig_Default)]
    Default = 0,
    [DynamicLocaleKey(LocaleKey.AnthropicRequestThinkingConfig_Disabled)]
    Disabled = 1,
    [DynamicLocaleKey(LocaleKey.AnthropicRequestThinkingConfig_Adaptive)]
    Adaptive = 2
}

public enum AnthropicRequestCacheControl
{
    [DynamicLocaleKey(LocaleKey.AnthropicRequestCacheControl_Ephemeral)]
    Ephemeral = 0,
    [DynamicLocaleKey(LocaleKey.AnthropicRequestCacheControl_NoCache)]
    NoCache = 1,
}

[GeneratedSettingsItems]
public sealed partial class AnthropicOptions : ObservableObject
{
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.AnthropicOptions_ThinkingConfig_Header,
        LocaleKey.AnthropicOptions_ThinkingConfig_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/build-with-claude/extended-thinking")]
    public partial AnthropicRequestThinkingConfig ThinkingConfig { get; set; } = AnthropicRequestThinkingConfig.Default;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.AnthropicOptions_BudgetTokens_Header,
        LocaleKey.AnthropicOptions_BudgetTokens_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/build-with-claude/extended-thinking")]
    public partial int BudgetTokens { get; set; } = 2048;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.AnthropicOptions_ThinkingEffort_Header,
        LocaleKey.AnthropicOptions_ThinkingEffort_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/build-with-claude/adaptive-thinking")]
    public partial string? ThinkingEffort { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.AnthropicOptions_CacheControl_Header,
        LocaleKey.AnthropicOptions_CacheControl_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/build-with-claude/prompt-caching")]
    public partial AnthropicRequestCacheControl CacheControl { get; set; } = AnthropicRequestCacheControl.Ephemeral;

    [DynamicLocaleKey(
        LocaleKey.Assistant_Temperature_Header,
        LocaleKey.Assistant_Temperature_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/api/beta/messages/create#create.temperature")]
    public string? Temperature { get; set; }

    [DynamicLocaleKey(
        LocaleKey.Assistant_TopP_Header,
        LocaleKey.Assistant_TopP_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/api/beta/messages/create#create.top_p")]
    public string? TopP { get; set; }

    [DynamicLocaleKey(
        LocaleKey.Assistant_TopK_Header,
        LocaleKey.Assistant_TopK_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/api/beta/messages/create#create.top_k")]
    public string? TopK { get; set; }
}
