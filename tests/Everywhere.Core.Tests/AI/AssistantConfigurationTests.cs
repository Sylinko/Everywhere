using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.NUnit;
using Everywhere.AI;
using Everywhere.AI.Configurator;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.Configuration.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Everywhere.Core.Tests.AI;

public class AssistantConfigurationTests
{
    [Test]
    public void PresetConfiguration_WhenRoundTripped_RestoresWithoutCatalog()
    {
        var assistant = CreatePreset();
        var json = JsonSerializer.Serialize(assistant);
        var restored = JsonSerializer.Deserialize<CustomAssistant>(json);

        Assert.That(restored, Is.Not.Null);
        Assert.That(restored.Name, Is.EqualTo(assistant.Name));
        Assert.That(JsonSerializer.Serialize(restored.Configuration), Is.EqualTo(JsonSerializer.Serialize(assistant.Configuration)));
        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.TryGetProperty("ModelId", out _), Is.False);
        Assert.That(document.RootElement.GetProperty("Configuration").GetProperty("$type").GetString(), Is.EqualTo("preset"));
    }

    [Test]
    public void LegacyMigration_WhenMovingConfiguration_PreservesCustomAssistantFields()
    {
        var root = JsonNode.Parse("""
        { "Model": { "CustomAssistants": [
          { "Id": "11111111-1111-1111-1111-111111111111", "Icon": { "Sentinel": "icon" },
            "Name": "Assistant name", "Description": "Assistant description",
            "SystemPromptId": "22222222-2222-2222-2222-222222222222", "IsToolCallEnabled": false,
            "ToolEnablementRulesets": { "Sentinel": "rules" }, "ContextCompressionThreshold": 65,
            "RequestTimeoutSeconds": 90, "OpenAIOptions": { "Sentinel": "openai" },
            "OpenAIResponsesOptions": { "Sentinel": "responses" },
            "AnthropicOptions": { "Sentinel": "anthropic" }, "GoogleOptions": { "Sentinel": "google" },
            "MistralOptions": { "Sentinel": "mistral" }, "ConfiguratorType": 0,
            "Endpoint": "https://example.com/v1", "Schema": "OpenAI", "ApiKey": "33333333-3333-3333-3333-333333333333",
            "ModelId": "model-id", "SupportsToolCall": true, "InputModalities": "Text",
            "OutputModalities": "Text", "ContextLimit": 32000, "OutputLimit": 4096,
            "Specializations": "None", "DeprecationDate": "2030-01-01" }
        ] } }
        """)!.AsObject();
        var assistant = root["Model"]!["CustomAssistants"]![0]!.AsObject();
        var preserved = new[]
        {
            "Id", "Icon", "Name", "Description", "SystemPromptId", "IsToolCallEnabled",
            "ToolEnablementRulesets", "ContextCompressionThreshold", "RequestTimeoutSeconds",
            "OpenAIOptions", "OpenAIResponsesOptions", "AnthropicOptions", "GoogleOptions", "MistralOptions"
        }.ToDictionary(name => name, name => assistant[name]?.DeepClone());

        new _20260908231914_0_8_2_canary_20260908_22().Migrate(root);

        Assert.Multiple(() =>
        {
            foreach (var property in preserved)
            {
                Assert.That(JsonNode.DeepEquals(assistant[property.Key], property.Value),
                    Is.True, $"Custom-assistant field {property.Key} changed during migration.");
            }

            Assert.That(assistant.ContainsKey("Endpoint"), Is.False);
            Assert.That(assistant.ContainsKey("ModelId"), Is.False);
            Assert.That(assistant.ContainsKey("ConfiguratorType"), Is.False);
            Assert.That(assistant["Configuration"]?["Endpoint"]?.GetValue<string>(), Is.EqualTo("https://example.com/v1"));
            Assert.That(assistant["Configuration"]?["ModelId"]?.GetValue<string>(), Is.EqualTo("model-id"));
        });
    }

    [Test]
    public void ModeSwitch_WhenReturningToPreset_RestoresTypedDraft()
    {
        var assistant = CreatePreset();
        var preset = assistant.Configuration;

        assistant.ConfiguratorType = AssistantConfiguratorType.Advanced;
        assistant.Configuration.ModelId = "custom-model";
        assistant.Configuration.Endpoint = "https://custom.example/v1";
        assistant.ConfiguratorType = AssistantConfiguratorType.PresetBased;

        Assert.That(assistant.Configuration, Is.SameAs(preset));
        assistant.ConfiguratorType = AssistantConfiguratorType.Advanced;
        Assert.That(assistant.Configuration.ModelId, Is.EqualTo("custom-model"));
    }

    [Test]
    public void SnapshotMapper_WhenAssistantChanges_KeepsModelAndProtocolOptions()
    {
        var assistant = CreatePreset();
        assistant.OpenAIOptions.ReasoningEffort = "low";
        var configuration = AssistantSnapshotMapper.Copy(assistant.Configuration);
        var options = AssistantSnapshotMapper.Copy(assistant.OpenAIOptions);

        assistant.Configuration.ModelId = "next-model";
        assistant.Configuration.InputModalities = Modalities.Text | Modalities.Image;
        assistant.OpenAIOptions.ReasoningEffort = "high";

        Assert.That(configuration.ModelId, Is.EqualTo("saved-model"));
        Assert.That(configuration.InputModalities, Is.EqualTo(Modalities.Text));
        Assert.That(options.ReasoningEffort, Is.EqualTo("low"));
    }

    [AvaloniaTest]
    public async Task Synchronization_WhenConfigurationReplacedOrRemoved_DoesNotUpdateOldOwner()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        var assistant = CreatePreset();
        settings.Model.CustomAssistants.Add(assistant);
        var provider = Substitute.For<IPresetModelProvider>();
        using var synchronizer = new AssistantConfigurationSynchronizer(settings, provider);
        await synchronizer.InitializeAsync();
        var updated = assistant.Configuration.ToTemplate() with { ContextLimit = 64000 };
        provider.GetValidatedModel("deepseek", "saved-model").Returns(updated);
        provider.ModelsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
        Assert.That(assistant.Configuration.ContextLimit, Is.EqualTo(64000));

        var persisted = assistant.Configuration;
        assistant.ConfiguratorType = AssistantConfiguratorType.Advanced;
        provider.GetValidatedModel("deepseek", "saved-model").Returns(updated with { ContextLimit = 128000 });
        provider.ModelsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
        Assert.That(assistant.Configuration.ContextLimit, Is.EqualTo(64000));

        assistant.Configuration = persisted;
        Assert.That(assistant.Configuration.ContextLimit, Is.EqualTo(128000));
        settings.Model.CustomAssistants = new ObservableCollection<CustomAssistant>();
        provider.GetValidatedModel("deepseek", "saved-model").Returns(updated with { ContextLimit = 256000 });
        provider.ModelsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
        Assert.That(assistant.Configuration.ContextLimit, Is.EqualTo(128000));
    }

    [AvaloniaTest]
    public async Task SettingsMigration_ThenCatalogSync_PersistsSnapshotAndDomesticId()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """
        { "Version": "0.8.1-canary.20260721.14", "Model": { "CustomAssistants": [
          { "Id": "11111111-1111-1111-1111-111111111111", "Name": "My MiniMax Assistant",
            "Description": "Assistant description", "ConfiguratorType": 1,
            "ModelProviderTemplateId": "minimax", "ModelDefinitionTemplateId": "saved-model",
            "ModelId": "saved-model", "Endpoint": "https://api.minimaxi.com/anthropic", "Schema": "Anthropic",
            "ContextLimit": 32000, "OutputLimit": 4096, "InputModalities": "Text", "OutputModalities": "Text" }
        ] } }
        """);
        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var settings = new Settings(services);
            using var engine = new SettingsEngine(settings, path, services, NullLoggerFactory.Instance);
            await engine.InitializeAsync();
            var assistant = settings.Model.CustomAssistants.Single();
            var preset = (PresetAssistantConfiguration)assistant.Configuration;
            Assert.That(assistant.Name, Is.EqualTo("My MiniMax Assistant"));
            Assert.That(assistant.Description, Is.EqualTo("Assistant description"));
            Assert.That(preset.ProviderId, Is.EqualTo("minimax-cn"));
            Assert.That(preset.ContextLimit, Is.EqualTo(32000));
            var provider = Substitute.For<IPresetModelProvider>();
            provider.GetValidatedModel("minimax-cn", "saved-model")
                .Returns(preset.ToTemplate() with { ContextLimit = 128000 });
            using var synchronizer = new AssistantConfigurationSynchronizer(settings, provider);
            await synchronizer.InitializeAsync();
            await engine.Storage.FlushAsync();
            var savedAssistant = JsonNode.Parse(await File.ReadAllTextAsync(path))?["Model"]?["CustomAssistants"]?[0];
            var saved = savedAssistant?["Configuration"];
            Assert.That(savedAssistant?["Name"]?.GetValue<string>(), Is.EqualTo("My MiniMax Assistant"));
            Assert.That(savedAssistant?["Description"]?.GetValue<string>(), Is.EqualTo("Assistant description"));
            Assert.That(saved?["$type"]?.GetValue<string>(), Is.EqualTo("preset"));
            Assert.That(saved?["ContextLimit"]?.GetValue<int>(), Is.EqualTo(128000));
            Assert.That(saved?["ProviderId"]?.GetValue<string>(), Is.EqualTo("minimax-cn"));
        }
        finally { File.Delete(path); }
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
