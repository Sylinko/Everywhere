using Avalonia.Headless.NUnit;
using Everywhere.AI;
using Everywhere.Cloud;
using Everywhere.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Everywhere.Core.Tests.AI;

public class AssistantCatalogTests
{
    [AvaloniaTest]
    public async Task Synchronizer_WhenOfficialCatalogChanges_UpdatesFutureConfigurationAndPreservesExistingSnapshot()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        var apiKey = Guid.CreateVersion7();
        var assistant = new CustomAssistant
        {
            Configuration = new OfficialAssistantConfiguration
            {
                ModelId = "model",
                ContextLimit = 100,
                ApiKey = apiKey,
                Schema = ModelProviderSchema.OpenAI
            }
        };
        assistant.OpenAIOptions.SelectedReasoningEffort = "high";
        assistant.OpenAIOptions.Temperature = "0.4";
        settings.Model.CustomAssistants.Add(assistant);

        var preset = Substitute.For<IPresetModelProvider>();
        preset.Catalog.Returns(PresetModelCatalog.Empty);
        var official = Substitute.For<IOfficialModelProvider>();
        var officialCatalog = CreateOfficialCatalog(CreateOfficialDefinition(200, ModelProviderSchema.OpenAIResponses));
        official.Catalog.Returns(_ => officialCatalog);
        var catalog = new AssistantCatalog(preset, official);
        using var synchronizer = new AssistantCatalogSynchronizer(settings, catalog, preset, official);

        await synchronizer.InitializeAsync();
        var requestSnapshot = AssistantSnapshotMapper.Copy(assistant.Configuration);
        officialCatalog = CreateOfficialCatalog(CreateOfficialDefinition(300, ModelProviderSchema.Anthropic));
        official.CatalogChanged += Raise.Event<EventHandler>(official, EventArgs.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assistant.Configuration.ContextLimit, Is.EqualTo(300));
            Assert.That(assistant.Configuration.Schema, Is.EqualTo(ModelProviderSchema.Anthropic));
            Assert.That(assistant.Configuration.ApiKey, Is.EqualTo(apiKey));
            Assert.That(assistant.OpenAIOptions.SelectedReasoningEffort, Is.EqualTo("high"));
            Assert.That(assistant.AnthropicOptions.DefaultReasoningEffortValues, Is.EqualTo(new[] { "low", "high" }));
            Assert.That(assistant.AnthropicOptions.EffectiveReasoningEffort, Is.EqualTo("high"));
            Assert.That(assistant.OpenAIOptions.Temperature, Is.EqualTo("0.4"));
            Assert.That(requestSnapshot.ContextLimit, Is.EqualTo(200));
            Assert.That(requestSnapshot.Schema, Is.EqualTo(ModelProviderSchema.OpenAIResponses));
        }

        officialCatalog = CreateOfficialCatalog();
        official.CatalogChanged += Raise.Event<EventHandler>(official, EventArgs.Empty);
        Assert.That(assistant.Configuration.ContextLimit, Is.EqualTo(300));
    }

    [AvaloniaTest]
    public async Task Synchronizer_WhenConfigurationIsReplaced_AppliesMatchingPresetDefinition()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        var assistant = new CustomAssistant();
        settings.Model.CustomAssistants.Add(assistant);

        var preset = Substitute.For<IPresetModelProvider>();
        var model = CreateModel(64000);
        preset.Catalog.Returns(CreatePresetCatalog("deepseek", model));
        var official = Substitute.For<IOfficialModelProvider>();
        official.Catalog.Returns(OfficialModelCatalog.Empty);
        var catalog = new AssistantCatalog(preset, official);
        using var synchronizer = new AssistantCatalogSynchronizer(settings, catalog, preset, official);
        await synchronizer.InitializeAsync();

        var apiKey = Guid.CreateVersion7();
        assistant.Configuration = new PresetAssistantConfiguration
        {
            ProviderId = "deepseek",
            ModelId = model.ModelId,
            ApiKey = apiKey
        };

        var provider = PresetModelTemplates.Providers.Single(template => template.Id == "deepseek");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(assistant.Configuration.ContextLimit, Is.EqualTo(64000));
            Assert.That(assistant.Configuration.Schema, Is.EqualTo(provider.Schema));
            Assert.That(assistant.Configuration.Endpoint, Is.EqualTo(provider.Endpoint));
            Assert.That(assistant.Configuration.ApiKey, Is.EqualTo(apiKey));
        }
    }

    private static OfficialModelCatalog CreateOfficialCatalog(params OfficialModelDefinition[] definitions) =>
        new(
            ModelCatalogSnapshot<OfficialModelDefinition, string>.Create(
                definitions.Select(static definition => KeyValuePair.Create(definition.Model.ModelId, definition))),
            true);

    private static OfficialModelDefinition CreateOfficialDefinition(int contextLimit, ModelProviderSchema schema) =>
        new(CreateModel(contextLimit), schema);

    private static ModelDefinitionTemplate CreateModel(int contextLimit) => new()
    {
        ModelId = "model",
        Name = "Model",
        SupportsToolCall = true,
        InputModalities = Modalities.Text,
        OutputModalities = Modalities.Text,
        ContextLimit = contextLimit,
        OutputLimit = 4096,
        ReasoningEffortValues = ["low", "high"]
    };

    private static PresetModelCatalog CreatePresetCatalog(
        string providerId,
        params ModelDefinitionTemplate[] definitions) =>
        new(
            [new PresetModelProviderCatalog(
                providerId,
                ModelCatalogSnapshot<ModelDefinitionTemplate, string>.Create(
                    definitions.Select(static definition => KeyValuePair.Create(definition.ModelId, definition))),
                PresetModelSourceStatus.Available,
                definitions.Select(static definition => definition.ModelId).ToHashSet(StringComparer.Ordinal))],
            true);
}
