using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

[GeneratedSettingsItems]
public sealed partial class GoogleOptions : ObservableObject
{
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.GoogleOptions_IncludeThoughts_Header,
        LocaleKey.GoogleOptions_IncludeThoughts_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/gemini-api/docs/thinking#summaries")]
    public partial bool IncludeThoughts { get; set; } = true;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.GoogleOptions_ThinkingLevel_Header,
        LocaleKey.GoogleOptions_ThinkingLevel_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/gemini-api/docs/thinking#thinking-levels")]
    public partial string? ThinkingLevel { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.GoogleOptions_ThinkingBudget_Header,
        LocaleKey.GoogleOptions_ThinkingBudget_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/gemini-api/docs/thinking#set-budget")]
    public partial string? ThinkingBudget { get; set; }

    [DynamicLocaleKey(
        LocaleKey.Assistant_Temperature_Header,
        LocaleKey.Assistant_Temperature_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/api/models#Model")]
    public string? Temperature { get; set; }

    [DynamicLocaleKey(
        LocaleKey.Assistant_TopP_Header,
        LocaleKey.Assistant_TopP_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/api/models#Model")]
    public string? TopP { get; set; }

    [DynamicLocaleKey(
        LocaleKey.Assistant_TopK_Header,
        LocaleKey.Assistant_TopK_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/api/models#Model")]
    public string? TopK { get; set; }
}
