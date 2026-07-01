using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

[GeneratedSettingsItems]
public sealed partial class OpenAIResponsesOptions : ObservableObject
{
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OpenAIResponsesOptions_ReasoningEffort_Header,
        LocaleKey.OpenAIResponsesOptions_ReasoningEffort_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://developers.openai.com/api/docs/guides/reasoning#reasoning-effort")]
    public partial string? ReasoningEffort { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OpenAIResponsesOptions_ReasoningSummary_Header,
        LocaleKey.OpenAIResponsesOptions_ReasoningSummary_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://developers.openai.com/api/docs/guides/reasoning#reasoning-summaries")]
    public partial string? ReasoningSummary { get; set; } = "auto";

    [DynamicLocaleKey(
        LocaleKey.Assistant_Temperature_Header,
        LocaleKey.Assistant_Temperature_Description)]
    [SettingsItem(
        Group = "_",
        DocumentUrl =
            "https://developers.openai.com/api/reference/resources/responses/methods/create#(resource)%20responses%20%3E%20(method)%20create%20%3E%20(params)%200.non_streaming%20%3E%20(param)%20temperature%20%3E%20(schema)")]
    public string? Temperature { get; set; }

    [DynamicLocaleKey(
        LocaleKey.Assistant_TopP_Header,
        LocaleKey.Assistant_TopP_Description)]
    [SettingsItem(
        Group = "_",
        DocumentUrl =
            "https://developers.openai.com/api/reference/resources/responses/methods/create#(resource)%20responses%20%3E%20(method)%20create%20%3E%20(params)%200.non_streaming%20%3E%20(param)%20top_p%20%3E%20(schema)")]
    public string? TopP { get; set; }
}
