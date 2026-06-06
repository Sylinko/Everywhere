using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

[GeneratedSettingsItems]
public sealed partial class OpenAIResponsesOptions : ObservableObject
{
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.OpenAIResponsesOptions_ReasoningEffort_Header,
        LocaleKey.OpenAIResponsesOptions_ReasoningEffort_Description)]
    [SettingsItem(DocumentUrl = "https://developers.openai.com/api/docs/guides/reasoning#reasoning-effort")]
    public partial string? ReasoningEffort { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.OpenAIResponsesOptions_ReasoningSummary_Header,
        LocaleKey.OpenAIResponsesOptions_ReasoningSummary_Description)]
    [SettingsItem(DocumentUrl = "https://developers.openai.com/api/docs/guides/reasoning#reasoning-summaries")]
    public partial string? ReasoningSummary { get; set; } = "auto";
}
