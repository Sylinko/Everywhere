using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

[GeneratedSettingsItems]
public sealed partial class OpenAIOptions : ObservableObject
{
    // OpenAI-compatible providers expose reasoning content with different field names and replay rules.
    // Keep provider-specific guidance in Everywhere docs instead of linking to one upstream vendor here.
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.OpenAIOptions_IncludeReasoningContent_Header,
        LocaleKey.OpenAIOptions_IncludeReasoningContent_Description)]
    public partial bool IncludeReasoningContent { get; set; } = true;

    // Non-standard OpenAI-compatible request body used by several reasoning model providers.
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.OpenAIOptions_ThinkingType_Header,
        LocaleKey.OpenAIOptions_ThinkingType_Description)]
    public partial string? ThinkingType { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.OpenAIOptions_ReasoningEffortLevel_Header,
        LocaleKey.OpenAIOptions_ReasoningEffortLevel_Description)]
    [SettingsItem(DocumentUrl = "https://platform.openai.com/docs/api-reference/chat/create-chat-completion")]
    public partial string? ReasoningEffortLevel { get; set; }
}
