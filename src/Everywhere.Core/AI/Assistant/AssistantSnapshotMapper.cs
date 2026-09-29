using Riok.Mapperly.Abstractions;

namespace Everywhere.AI;

/// <summary>
/// Creates data-only copies used by a KernelMixin for its complete lifetime.
/// Generated mappings keep snapshot membership aligned with the configuration types.
/// </summary>
[Mapper(UseDeepCloning = true, RequiredMappingStrategy = RequiredMappingStrategy.Both)]
internal static partial class AssistantSnapshotMapper
{
    public static AssistantConfiguration Copy(AssistantConfiguration source) => source switch
    {
        OfficialAssistantConfiguration official => Copy(official),
        PresetAssistantConfiguration preset => Copy(preset),
        AdvancedAssistantConfiguration advanced => Copy(advanced),
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    [MapperIgnoreSource(nameof(AssistantConfiguration.DefaultReasoningEffortValues))]
    [MapperIgnoreTarget(nameof(AssistantConfiguration.DefaultReasoningEffortValues))]
    private static partial OfficialAssistantConfiguration MapToOfficial(AssistantConfiguration source);

    public static OfficialAssistantConfiguration ToOfficial(AssistantConfiguration source) => MapToOfficial(source);

    [MapperIgnoreTarget(nameof(PresetAssistantConfiguration.ProviderId))]
    [MapperIgnoreSource(nameof(AssistantConfiguration.DefaultReasoningEffortValues))]
    [MapperIgnoreTarget(nameof(AssistantConfiguration.DefaultReasoningEffortValues))]
    private static partial PresetAssistantConfiguration MapToPreset(AssistantConfiguration source);

    public static PresetAssistantConfiguration ToPreset(AssistantConfiguration source)
    {
        var target = MapToPreset(source);
        target.ProviderId = (source as PresetAssistantConfiguration)?.ProviderId;
        return target;
    }

    [MapperIgnoreSource(nameof(AssistantConfiguration.DefaultReasoningEffortValues))]
    [MapperIgnoreTarget(nameof(AssistantConfiguration.DefaultReasoningEffortValues))]
    private static partial AdvancedAssistantConfiguration MapToAdvanced(AssistantConfiguration source);

    public static AdvancedAssistantConfiguration ToAdvanced(AssistantConfiguration source) => MapToAdvanced(source);

    public static partial OfficialAssistantConfiguration Copy(OfficialAssistantConfiguration source);
    public static partial PresetAssistantConfiguration Copy(PresetAssistantConfiguration source);
    public static partial AdvancedAssistantConfiguration Copy(AdvancedAssistantConfiguration source);

    public static partial OpenAIOptions Copy(OpenAIOptions source);
    public static partial OpenAIResponsesOptions Copy(OpenAIResponsesOptions source);
    public static partial AnthropicOptions Copy(AnthropicOptions source);
    public static partial GoogleOptions Copy(GoogleOptions source);
    public static partial MistralOptions Copy(MistralOptions source);
}