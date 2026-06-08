using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

[GeneratedSettingsItems]
public sealed partial class GoogleOptions : ObservableObject
{
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.GoogleOptions_IncludeThoughts_Header,
        LocaleKey.GoogleOptions_IncludeThoughts_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/gemini-api/docs/thinking#summaries")]
    public partial bool IncludeThoughts { get; set; } = true;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.GoogleOptions_ThinkingLevel_Header,
        LocaleKey.GoogleOptions_ThinkingLevel_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/gemini-api/docs/thinking#thinking-levels")]
    public partial string? ThinkingLevel { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.GoogleOptions_ThinkingBudget_Header,
        LocaleKey.GoogleOptions_ThinkingBudget_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://ai.google.dev/gemini-api/docs/thinking#set-budget")]
    public partial string? ThinkingBudget { get; set; }
}
