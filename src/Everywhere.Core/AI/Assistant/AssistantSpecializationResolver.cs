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
        if (specialization is ModelSpecializations.Default or ModelSpecializations.ToolApproval) return currentAssistant;

        var sourceConfiguration = currentAssistant.Configuration;
        lock (sourceConfiguration)
        {
            var currentModel = catalog.ResolveModel(sourceConfiguration);
            var selectedSpecializations = currentModel.Model?.Specializations ?? sourceConfiguration.Specializations;
            if (selectedSpecializations.HasFlag(specialization)) return currentAssistant;

            var candidate = catalog.ResolveSpecializedModel(sourceConfiguration, specialization);
            if (candidate.Model is not { } model || candidate.Schema is not { } schema) return currentAssistant;

            var configuration = CreateConfiguration(sourceConfiguration);
            if (configuration is null) return currentAssistant;

            lock (configuration)
            {
                configuration.Schema = schema;
                configuration.Apply(model);
                if (configuration is PresetAssistantConfiguration preset) preset.Endpoint = candidate.Endpoint;
            }

            return new SystemAssistant(specialization) { Configuration = configuration };
        }
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