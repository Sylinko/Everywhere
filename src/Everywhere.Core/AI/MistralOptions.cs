using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

[GeneratedSettingsItems]
public sealed partial class MistralOptions : ObservableObject
{
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.MistralOptions_IncludeReasoningContent_Header,
        LocaleKey.MistralOptions_IncludeReasoningContent_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://docs.mistral.ai/capabilities/reasoning")]
    public partial bool IncludeReasoningContent { get; set; } = true;

    [DynamicResourceKey(
        LocaleKey.Assistant_Temperature_Header,
        LocaleKey.Assistant_Temperature_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://docs.mistral.ai/api#tag/chat/operation/chat_completion_v1_chat_completions_post")]
    public string? Temperature { get; set; }

    [DynamicResourceKey(
        LocaleKey.Assistant_TopP_Header,
        LocaleKey.Assistant_TopP_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://docs.mistral.ai/api#tag/chat/operation/chat_completion_v1_chat_completions_post")]
    public string? TopP { get; set; }
}
