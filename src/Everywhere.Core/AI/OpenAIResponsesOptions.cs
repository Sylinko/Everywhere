using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

[GeneratedSettingsItems]
public sealed partial class OpenAIResponsesOptions : ReasoningModelSchemaOptions
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override ModelProviderSchema Schema => ModelProviderSchema.OpenAIResponses;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasoningEffortValues))]
    [NotifyPropertyChangedFor(nameof(EffectiveReasoningEffort))]
    [DynamicLocaleKey(
        LocaleKey.OpenAIResponsesOptions_ReasoningEffort_Header,
        LocaleKey.OpenAIResponsesOptions_ReasoningEffort_Description)]
    [SettingsItem(
        Group = "_",
        Modifier = nameof(BindReasoningEffortPlaceholder),
        DocumentUrl = "https://developers.openai.com/api/docs/guides/reasoning#reasoning-effort")]
    public override partial string? ReasoningEffort { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OpenAIResponsesOptions_ReasoningSummary_Header,
        LocaleKey.OpenAIResponsesOptions_ReasoningSummary_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://developers.openai.com/api/docs/guides/reasoning#reasoning-summaries")]
    public partial string? ReasoningSummary { get; set; } = "auto";

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.Assistant_Temperature_Header,
        LocaleKey.Assistant_Temperature_Description)]
    [SettingsItem(
        Group = "_",
        DocumentUrl =
            "https://developers.openai.com/api/reference/resources/responses/methods/create#(resource)%20responses%20%3E%20(method)%20create%20%3E%20(params)%200.non_streaming%20%3E%20(param)%20temperature%20%3E%20(schema)")]
    public partial string? Temperature { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.Assistant_TopP_Header,
        LocaleKey.Assistant_TopP_Description)]
    [SettingsItem(
        Group = "_",
        DocumentUrl =
            "https://developers.openai.com/api/reference/resources/responses/methods/create#(resource)%20responses%20%3E%20(method)%20create%20%3E%20(params)%200.non_streaming%20%3E%20(param)%20top_p%20%3E%20(schema)")]
    public partial string? TopP { get; set; }
}