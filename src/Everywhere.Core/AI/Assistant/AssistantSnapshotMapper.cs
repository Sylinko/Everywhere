using Riok.Mapperly.Abstractions;

namespace Everywhere.AI;

/// <summary>
/// Creates data-only copies used by a KernelMixin for its complete lifetime.
/// Generated mappings keep snapshot membership aligned with the configuration types.
/// </summary>
[Mapper(UseDeepCloning = true, RequiredMappingStrategy = RequiredMappingStrategy.Both)]
internal static partial class AssistantSnapshotMapper
{
    [MapDerivedType<OfficialAssistantConfiguration, OfficialAssistantConfiguration>]
    [MapDerivedType<PresetAssistantConfiguration, PresetAssistantConfiguration>]
    [MapDerivedType<AdvancedAssistantConfiguration, AdvancedAssistantConfiguration>]
    public static partial AssistantConfiguration Copy(AssistantConfiguration source);

    public static partial OfficialAssistantConfiguration ToOfficial(AssistantConfiguration source);

    [MapperIgnoreTarget(nameof(PresetAssistantConfiguration.ProviderId))]
    public static partial PresetAssistantConfiguration ToPreset(AssistantConfiguration source);

    public static partial AdvancedAssistantConfiguration ToAdvanced(AssistantConfiguration source);

    public static partial OpenAIOptions Copy(OpenAIOptions source);
    public static partial OpenAIResponsesOptions Copy(OpenAIResponsesOptions source);
    public static partial AnthropicOptions Copy(AnthropicOptions source);
    public static partial GoogleOptions Copy(GoogleOptions source);
    public static partial MistralOptions Copy(MistralOptions source);
}