using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

/// <summary>
/// Provides configurable options for Mistral AI chat completion models.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class MistralOptions : ObservableObject
{
    /// <summary>
    /// Gets or sets a value indicating whether reasoning content is included in responses.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.MistralOptions_IncludeReasoningContent_Header,
        LocaleKey.MistralOptions_IncludeReasoningContent_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://docs.mistral.ai/capabilities/reasoning")]
    public partial bool IncludeReasoningContent { get; set; } = true;

    /// <summary>
    /// Gets or sets the amount of reasoning effort requested from the model.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.MistralOptions_ReasoningEffort_Header,
        LocaleKey.MistralOptions_ReasoningEffort_Description)]
    [SettingsItem(
        Group = "_",
        IsEnabledBindingPath = nameof(IncludeReasoningContent),
        DocumentUrl = "https://docs.mistral.ai/capabilities/reasoning")]
    public partial string? ReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets the sampling temperature passed to the model.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.Assistant_Temperature_Header,
        LocaleKey.Assistant_Temperature_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://docs.mistral.ai/api#tag/chat/operation/chat_completion_v1_chat_completions_post")]
    public partial string? Temperature { get; set; }

    /// <summary>
    /// Gets or sets the nucleus sampling probability passed to the model.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.Assistant_TopP_Header,
        LocaleKey.Assistant_TopP_Description)]
    [SettingsItem(Group = "_", DocumentUrl = "https://docs.mistral.ai/api#tag/chat/operation/chat_completion_v1_chat_completions_post")]
    public partial string? TopP { get; set; }
}
