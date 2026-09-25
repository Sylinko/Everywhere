using System.Text.Json.Nodes;
using Everywhere.AI;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration.Engine;
using PresetModelTemplates = Everywhere.AI.PresetModelTemplates;

namespace Everywhere.Configuration.Migrations;

public sealed class _20260908231914_0_8_2_canary_20260908_22 : SettingsMigration
{
    public override SemanticVersion Version => new(0, 8, 2, 0, "canary.20260908.22");

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [MigrateAssistants];

    private static readonly string[] LegacyConnectionFields =
    [
        "Endpoint", "Schema", "ApiKey"
    ];

    private static readonly string[] LegacyModelFields =
    [
        // This is the frozen field set from the legacy Assistant schema. Do not add
        // current AssistantConfiguration fields here: CustomAssistant owns Name at this level.
        "ModelId", "SupportsToolCall", "InputModalities", "OutputModalities",
        "ContextLimit", "OutputLimit", "Specializations", "DeprecationDate"
    ];

    private static readonly string[] SnapshotModelFields =
    [
        "ModelId", "SupportsToolCall", "InputModalities", "OutputModalities",
        "ContextLimit", "OutputLimit", "Specializations", "DeprecationDate",
        "Name", "ReleaseDate", "KnowledgeCutoff"
    ];

    private static bool MigrateAssistants(JsonObject root)
    {
        var changed = false;
        if (root["Model"]?["CustomAssistants"] is JsonArray assistants)
        {
            foreach (var assistant in assistants.AsValueEnumerable().OfType<JsonObject>())
            {
                changed |= MigrateAssistant(assistant);
            }
        }

        if (root["SystemAssistant"] is JsonObject system)
        {
            foreach (var assistant in system.AsValueEnumerable().Select(p => p.Value).OfType<JsonObject>())
            {
                changed |= MigrateAssistant(assistant);
            }
        }

        return changed;
    }

    private static bool MigrateAssistant(JsonObject assistant)
    {
        if (assistant["Configuration"] is JsonObject existing)
        {
            return FlattenIntermediateConfiguration(assistant, existing) | NormalizeConfiguration(existing);
        }

        var mode = assistant["ConfiguratorType"]?.ToString().ToLowerInvariant();
        var type = mode switch
        {
            "1" or "presetbased" => "preset",
            null or "2" or "official" => "official",
            _ => "advanced"
        };
        var providerId = assistant["ModelProviderTemplateId"]?.ToString() switch
        {
            "moonshot" => "moonshotai-cn",
            "minimax" => "minimax-cn",
            "siliconcloud" => "siliconflow-cn",
            var id => id
        };
        var modelId = assistant["ModelId"]?.ToString();
        if (string.IsNullOrWhiteSpace(modelId)) modelId = assistant["ModelDefinitionTemplateId"]?.ToString();
        var provider = type == "preset" ? PresetModelTemplates.Providers.AsValueEnumerable().FirstOrDefault(p => p.Id == providerId) : null;
        var fallback = provider?.ModelDefinitions.FirstOrDefault(m => m.ModelId == modelId);
        var configuration = new JsonObject { ["$type"] = type };
        if (fallback is not null)
        {
            AssistantConfiguration fallbackConfiguration = new AdvancedAssistantConfiguration();
            lock (fallbackConfiguration)
            {
                fallbackConfiguration.Apply(fallback);
            }
            var fallbackNode = SettingsEngineJson.SerializeToNode(fallbackConfiguration, typeof(AssistantConfiguration))?.AsObject();
            if (fallbackNode is not null)
            {
                foreach (var name in SnapshotModelFields)
                {
                    if (fallbackNode.TryGetPropertyValue(name, out var value))
                    {
                        configuration[name] = value?.DeepClone();
                    }
                }
            }
        }

        foreach (var name in LegacyModelFields)
        {
            if (assistant.TryGetPropertyValue(name, out var value))
            {
                configuration[name] = value?.DeepClone();
            }
        }

        configuration["ModelId"] = modelId;
        if (type == "preset") configuration["ProviderId"] = providerId;
        if (provider is not null)
        {
            configuration["Endpoint"] = provider.Endpoint;
            configuration["Schema"] = provider.Schema.ToString();
            if (!assistant.ContainsKey("RequestTimeoutSeconds"))
            {
                assistant["RequestTimeoutSeconds"] = provider.RequestTimeoutSeconds;
            }
        }

        foreach (var name in LegacyConnectionFields)
        {
            if (assistant.TryGetPropertyValue(name, out var value))
            {
                configuration[name] = value?.DeepClone();
            }
        }

        assistant["Configuration"] = configuration;
        foreach (var name in LegacyConnectionFields.AsValueEnumerable().Concat(LegacyModelFields).Concat(["ConfiguratorType", "ModelProviderTemplateId", "ModelDefinitionTemplateId"]))
        {
            assistant.Remove(name);
        }

        NormalizeConfiguration(configuration);

        return true;
    }

    private static bool NormalizeConfiguration(JsonObject configuration)
    {
        var type = configuration["$type"]?.ToString();
        if (type is not ("official" or "preset")) return false;

        var changed = false;
        if (configuration["LastKnownModel"] is JsonObject snapshot)
        {
            foreach (var name in SnapshotModelFields)
            {
                if (configuration.ContainsKey(name) || !snapshot.TryGetPropertyValue(name, out var value)) continue;
                configuration[name] = value?.DeepClone();
                changed = true;
            }

            configuration.Remove("LastKnownModel");
            changed = true;
        }

        changed |= configuration.Remove("CurrentModel");
        changed |= configuration.Remove("CatalogState");

        if (type == "official")
        {
            changed |= configuration.Remove("Endpoint");
            changed |= configuration.Remove("ApiKey");
            var schema = OfficialModelDefinition.InferLegacySchemaFromModelId(configuration["ModelId"]?.ToString()).ToString();
            if (configuration["Schema"]?.ToString() != schema)
            {
                configuration["Schema"] = schema;
                changed = true;
            }
        }

        return changed;
    }

    private static bool FlattenIntermediateConfiguration(JsonObject assistant, JsonObject configuration)
    {
        var changed = false;
        if (configuration["Model"] is JsonObject model)
        {
            foreach (var property in model.ToArray())
            {
                if (!configuration.ContainsKey(property.Key))
                {
                    configuration[property.Key] = property.Value?.DeepClone();
                }
            }
            configuration.Remove("Model");
            changed = true;
        }

        if (configuration.TryGetPropertyValue("RequestTimeoutSeconds", out var timeout))
        {
            if (!assistant.ContainsKey("RequestTimeoutSeconds"))
            {
                assistant["RequestTimeoutSeconds"] = timeout?.DeepClone();
            }

            configuration.Remove("RequestTimeoutSeconds");
            changed = true;
        }

        return changed;
    }
}