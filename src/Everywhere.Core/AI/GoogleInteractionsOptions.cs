using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

[GeneratedSettingsItems]
public sealed partial class GoogleInteractionsOptions : ReasoningModelSchemaOptions
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override ModelProviderSchema Schema => ModelProviderSchema.GoogleInteractions;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.GoogleInteractionsOptions_IncludeThoughts_Header,
        LocaleKey.GoogleInteractionsOptions_IncludeThoughts_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/gemini-api/docs/interactions")]
    public partial bool IncludeThoughts { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasoningEffort))]
    [DynamicLocaleKey(
        LocaleKey.GoogleInteractionsOptions_ThinkingLevel_Header,
        LocaleKey.GoogleInteractionsOptions_ThinkingLevel_Description)]
    [SettingsItem(
        Group = "_",
        Modifier = nameof(RegisterReasoningEffortSettingsItem),
        DocumentUrl = "https://ai.google.dev/gemini-api/docs/thinking#thinking-levels")]
    public partial string? ThinkingLevel { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.Assistant_Temperature_Header,
        LocaleKey.Assistant_Temperature_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/api/interactions")]
    public partial string? Temperature { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.Assistant_TopP_Header,
        LocaleKey.Assistant_TopP_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/api/interactions")]
    public partial string? TopP { get; set; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string? ReasoningEffort
    {
        get => ThinkingLevel;
        set => ThinkingLevel = value;
    }
}
