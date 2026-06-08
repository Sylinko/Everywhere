using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

public enum AnthropicRequestThinkingConfig
{
    [DynamicResourceKey(LocaleKey.AnthropicRequestThinkingConfig_Default)]
    Default = 0,
    [DynamicResourceKey(LocaleKey.AnthropicRequestThinkingConfig_Disabled)]
    Disabled = 1,
    [DynamicResourceKey(LocaleKey.AnthropicRequestThinkingConfig_Adaptive)]
    Adaptive = 2
}

public enum AnthropicRequestCacheControl
{
    [DynamicResourceKey(LocaleKey.AnthropicRequestCacheControl_Ephemeral)]
    Ephemeral = 0,
    [DynamicResourceKey(LocaleKey.AnthropicRequestCacheControl_NoCache)]
    NoCache = 1,
}

[GeneratedSettingsItems]
public sealed partial class AnthropicOptions : ObservableObject
{
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.AnthropicOptions_ThinkingConfig_Header,
        LocaleKey.AnthropicOptions_ThinkingConfig_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/build-with-claude/extended-thinking")]
    public partial AnthropicRequestThinkingConfig ThinkingConfig { get; set; } = AnthropicRequestThinkingConfig.Default;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.AnthropicOptions_BudgetTokens_Header,
        LocaleKey.AnthropicOptions_BudgetTokens_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/build-with-claude/extended-thinking")]
    public partial int BudgetTokens { get; set; } = 2048;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.AnthropicOptions_ThinkingEffort_Header,
        LocaleKey.AnthropicOptions_ThinkingEffort_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/build-with-claude/adaptive-thinking")]
    public partial string? ThinkingEffort { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.AnthropicOptions_CacheControl_Header,
        LocaleKey.AnthropicOptions_CacheControl_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://platform.claude.com/docs/en/build-with-claude/prompt-caching")]
    public partial AnthropicRequestCacheControl CacheControl { get; set; } = AnthropicRequestCacheControl.Ephemeral;
}
