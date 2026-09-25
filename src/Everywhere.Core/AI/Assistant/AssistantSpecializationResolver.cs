namespace Everywhere.AI;

/// <summary>
/// Resolves the model used by system tasks. This is a runtime policy and does not depend on the
/// configuration UI or its temporary mode drafts.
/// </summary>
public sealed class AssistantSpecializationResolver(AssistantCatalog catalog)
{
    public Assistant Resolve(SystemAssistant systemAssistant, Assistant currentAssistant)
    {
        if (!systemAssistant.AutoSelect) return systemAssistant;

        var specialization = systemAssistant.RequiredSpecializations;
        if (specialization == ModelSpecializations.Default) return currentAssistant;

        var currentModel = catalog.ResolveModel(currentAssistant.Configuration);
        var selectedSpecializations = currentModel.Model?.Specializations ??
            currentAssistant.Configuration.Specializations;
        if (selectedSpecializations.HasFlag(specialization)) return currentAssistant;

        var candidate = catalog.ResolveSpecializedModel(currentAssistant.Configuration, specialization);
        if (candidate.Model is not { } model || candidate.Schema is not { } schema) return currentAssistant;

        var configuration = CreateConfiguration(currentAssistant.Configuration);
        if (configuration is null) return currentAssistant;

        lock (configuration)
        {
            configuration.Schema = schema;
            configuration.Apply(model);
            if (configuration is PresetAssistantConfiguration preset) preset.Endpoint = candidate.Endpoint;
        }

        var resolved = new SystemAssistant(specialization) { Configuration = configuration };
        resolved.EffectiveReasoningOptions?.DefaultReasoningEffortValues = model.ReasoningEffortValues;
        return resolved;
    }

    private static AssistantConfiguration? CreateConfiguration(AssistantConfiguration source) => source switch
    {
        OfficialAssistantConfiguration => new OfficialAssistantConfiguration(),
        PresetAssistantConfiguration preset => new PresetAssistantConfiguration
        {
            ApiKey = preset.ApiKey,
            ProviderId = preset.ProviderId
        },
        _ => null
    };
}