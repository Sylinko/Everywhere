using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.AI;
using Everywhere.Configuration.Migrations;
using Everywhere.Views;

namespace Everywhere.Core.Tests.AI;

public class AssistantConfigurationTests
{
    [Test]
    public void PresetConfiguration_WhenRoundTripped_RemainsSelfContained()
    {
        var assistant = CreatePreset();
        var json = JsonSerializer.Serialize(assistant);
        var restored = JsonSerializer.Deserialize<CustomAssistant>(json);

        Assert.That(restored, Is.Not.Null);
        Assert.That(JsonSerializer.Serialize(restored.Configuration), Is.EqualTo(JsonSerializer.Serialize(assistant.Configuration)));
        using var document = JsonDocument.Parse(json);
        var persisted = document.RootElement.GetProperty(nameof(Assistant.Configuration));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.GetProperty("$type").GetString(), Is.EqualTo("preset"));
            Assert.That(persisted.GetProperty(nameof(AssistantConfiguration.ContextLimit)).GetInt32(), Is.EqualTo(32000));
            Assert.That(persisted.TryGetProperty("ReasoningEffortValues", out _), Is.False);
            Assert.That(persisted.TryGetProperty("LastKnownModel", out _), Is.False);
            Assert.That(document.RootElement.TryGetProperty(nameof(AssistantConfiguration.ModelId), out _), Is.False);
        }
    }

    [Test]
    public void SnapshotMapper_WhenSourceChanges_KeepsDetachedConfiguration()
    {
        var source = (PresetAssistantConfiguration)CreatePreset().Configuration;
        var snapshot = (PresetAssistantConfiguration)AssistantSnapshotMapper.Copy(source);

        source.ModelId = "next-model";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.ProviderId, Is.EqualTo("deepseek"));
            Assert.That(snapshot.ModelId, Is.EqualTo("saved-model"));
        }
    }

    [Test]
    public void ConfigurationSelector_WhenReturningToMode_RestoresItsDraft()
    {
        var assistant = new CustomAssistant
        {
            Configuration = new OfficialAssistantConfiguration
            {
                ModelId = "official-model"
            }
        };
        var selector = new AssistantConfiguratorSelector
        {
            Assistant = assistant
        };
        var presetMode = selector.ConfiguratorModels.Single(model =>
            model.Mode == AssistantConfiguratorSelector.ConfigurationMode.Preset);
        var advancedMode = selector.ConfiguratorModels.Single(model =>
            model.Mode == AssistantConfiguratorSelector.ConfigurationMode.Advanced);

        selector.SelectedConfiguratorModel = presetMode;
        var presetDraft = assistant.Configuration;
        presetDraft.ModelId = "preset-model";
        selector.SelectedConfiguratorModel = advancedMode;
        selector.SelectedConfiguratorModel = presetMode;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assistant.Configuration, Is.SameAs(presetDraft));
            Assert.That(assistant.Configuration.ModelId, Is.EqualTo("preset-model"));
        }
    }

    [Test]
    public void LegacyMigration_WhenMovingConfiguration_PreservesAssistantAndModelFields()
    {
        var root = JsonNode.Parse("""
        { "Model": { "CustomAssistants": [
          { "Name": "Assistant name", "Description": "Assistant description", "ConfiguratorType": 0,
            "Endpoint": "https://example.com/v1", "Schema": "OpenAI",
            "ModelId": "model-id", "SupportsToolCall": true, "InputModalities": "Text",
            "OutputModalities": "Text", "ContextLimit": 32000, "OutputLimit": 4096 }
        ] } }
        """)!.AsObject();

        new _20260908231914_0_8_2_canary_20260908_22().Migrate(root);

        var assistant = root["Model"]?["CustomAssistants"]?[0];
        var configuration = assistant?["Configuration"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(assistant?["Name"]?.GetValue<string>(), Is.EqualTo("Assistant name"));
            Assert.That(assistant?["Description"]?.GetValue<string>(), Is.EqualTo("Assistant description"));
            Assert.That(assistant?["ModelId"], Is.Null);
            Assert.That(configuration?["$type"]?.GetValue<string>(), Is.EqualTo("advanced"));
            Assert.That(configuration?["Endpoint"]?.GetValue<string>(), Is.EqualTo("https://example.com/v1"));
            Assert.That(configuration?["ModelId"]?.GetValue<string>(), Is.EqualTo("model-id"));
            Assert.That(configuration?["ContextLimit"]?.GetValue<int>(), Is.EqualTo(32000));
        }
    }

    [Test]
    public void LegacyMigration_WhenIntermediateSnapshotExists_FlattensItAndInfersOfficialSchema()
    {
        var root = JsonNode.Parse("""
        { "Model": { "CustomAssistants": [
          { "Configuration": { "$type": "official", "ModelId": "anthropic/claude-test",
              "Endpoint": "https://obsolete.example", "LastKnownModel": {
                "Name": "Claude Test", "ContextLimit": 100000,
                "ReasoningEffortValues": ["low", "high"] } } }
        ] } }
        """)!.AsObject();

        new _20260908231914_0_8_2_canary_20260908_22().Migrate(root);

        var configuration = root["Model"]?["CustomAssistants"]?[0]?["Configuration"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration?["Schema"]?.GetValue<string>(), Is.EqualTo("Anthropic"));
            Assert.That(configuration?["Endpoint"], Is.Null);
            Assert.That(configuration?["LastKnownModel"], Is.Null);
            Assert.That(configuration?["Name"]?.GetValue<string>(), Is.EqualTo("Claude Test"));
            Assert.That(configuration?["ContextLimit"]?.GetValue<int>(), Is.EqualTo(100000));
            Assert.That(configuration?["ReasoningEffortValues"], Is.Null);
        }
    }

    private static CustomAssistant CreatePreset() => new()
    {
        Name = "Assistant name",
        Configuration = new PresetAssistantConfiguration
        {
            ProviderId = "deepseek",
            Endpoint = "https://api.deepseek.com",
            Schema = ModelProviderSchema.OpenAI,
            ModelId = "saved-model",
            Name = "Saved model",
            ContextLimit = 32000,
            OutputLimit = 4096,
            InputModalities = Modalities.Text,
            OutputModalities = Modalities.Text
        }
    };
}
