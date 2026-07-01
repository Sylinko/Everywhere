using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

[GeneratedSettingsItems]
public sealed partial class OpenAIOptions : ObservableObject
{
    // OpenAI-compatible providers expose reasoning content with different field names and replay rules.
    // Keep provider-specific guidance in Everywhere docs instead of linking to one upstream vendor here.
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OpenAIOptions_IncludeReasoningContent_Header,
        LocaleKey.OpenAIOptions_IncludeReasoningContent_Description)]
    [SettingsItem(Group = "_")]
    public partial bool IncludeReasoningContent { get; set; } = true;

    // Non-standard OpenAI-compatible request body used by several reasoning model providers.
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OpenAIOptions_ThinkingType_Header,
        LocaleKey.OpenAIOptions_ThinkingType_Description)]
    [SettingsItem(Group = "_")]
    public partial string? ThinkingType { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OpenAIOptions_ReasoningEffort_Header,
        LocaleKey.OpenAIOptions_ReasoningEffort_Description)]
    [SettingsItem(
        Group = "_",
        DocumentUrl =
            "https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create#(resource)%20chat.completions%20%3E%20(method)%20create%20%3E%20(params)%200.non_streaming%20%3E%20(param)%20reasoning_effort%20%3E%20(schema)")]
    public partial string? ReasoningEffort { get; set; }

    [DynamicLocaleKey(
        LocaleKey.Assistant_Temperature_Header,
        LocaleKey.Assistant_Temperature_Description)]
    [SettingsItem(
        Group = "_",
        DocumentUrl =
            "https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create#(resource)%20chat.completions%20%3E%20(method)%20create%20%3E%20(params)%200.non_streaming%20%3E%20(param)%20temperature%20%3E%20(schema)")]
    public string? Temperature { get; set; }

    [DynamicLocaleKey(
        LocaleKey.Assistant_TopP_Header,
        LocaleKey.Assistant_TopP_Description)]
    [SettingsItem(
        Group = "_",
        DocumentUrl =
            "https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create#(resource)%20chat.completions%20%3E%20(method)%20create%20%3E%20(params)%200.non_streaming%20%3E%20(param)%20top_p%20%3E%20(schema)")]
    public string? TopP { get; set; }

    [DynamicLocaleKey(
        LocaleKey.Assistant_PresencePenalty_Header,
        LocaleKey.Assistant_PresencePenalty_Description)]
    [SettingsItem(
        Group = "_",
        DocumentUrl =
            "https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create#(resource)%20chat.completions%20%3E%20(method)%20create%20%3E%20(params)%200.non_streaming%20%3E%20(param)%20presence_penalty%20%3E%20(schema)")]
    public string? PresencePenalty { get; set; }

    [DynamicLocaleKey(
        LocaleKey.Assistant_FrequencyPenalty_Header,
        LocaleKey.Assistant_FrequencyPenalty_Description)]
    [SettingsItem(
        Group = "_",
        DocumentUrl =
            "https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create#(resource)%20chat.completions%20%3E%20(method)%20create%20%3E%20(params)%200.non_streaming%20%3E%20(param)%20frequency_penalty%20%3E%20(schema)")]
    public string? FrequencyPenalty { get; set; }
}