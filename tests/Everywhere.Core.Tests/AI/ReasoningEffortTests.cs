using System.Text.Json;
using Everywhere.AI;
using MessagePack;

namespace Everywhere.Core.Tests.AI;

public sealed class ReasoningEffortTests
{
    [Test]
    public void Options_WhenChoicesChange_PreserveOrderAndResolveUnavailableSelectionToMiddleRight()
    {
        var options = new OpenAIOptions
        {
            ReasoningEffort = " high ｜ | low | high | MEDIUM ",
            SelectedReasoningEffort = "missing"
        };

        Assert.Multiple(() =>
        {
            Assert.That(options.ReasoningEffortValues, Is.EqualTo(new[] { "high", "low", "high", "MEDIUM" })); // preserves order and duplicates
            Assert.That(options.EffectiveReasoningEffort, Is.EqualTo("high"));
            Assert.That(options.SelectedReasoningEffort, Is.EqualTo("missing"));
        });

        options.ReasoningEffort = "a|b|c|d";
        Assert.That(options.EffectiveReasoningEffort, Is.EqualTo("c"));
    }

    [Test]
    public void Options_WhenUserValuesAreEmpty_UseTransientCatalogDefaults()
    {
        var options = new OpenAIOptions
        {
            SelectedReasoningEffort = "removed",
            DefaultReasoningEffortValues = ["low", "medium", "high", "max"]
        };

        Assert.Multiple(() =>
        {
            Assert.That(options.ReasoningEffortValues, Is.EqualTo(new[] { "low", "medium", "high", "max" }));
            Assert.That(options.EffectiveReasoningEffort, Is.EqualTo("high"));
            Assert.That(options.ReasoningEffortPlaceholder, Is.EqualTo("low|medium|high|max"));
            Assert.That(options.SelectedReasoningEffort, Is.EqualTo("removed"));
        });

        options.ReasoningEffort = "custom|maximum";
        Assert.That(options.ReasoningEffortValues, Is.EqualTo(new[] { "custom", "maximum" }));
        Assert.That(options.EffectiveReasoningEffort, Is.EqualTo("maximum"));
        Assert.That(options.ReasoningEffortPlaceholder, Is.EqualTo("low|medium|high|max"));
    }

    [Test]
    public void Assistant_WhenSchemaChanges_ExposesProtocolReasoningOptionsWithoutForwardingTheirChanges()
    {
        var assistant = new CustomAssistant
        {
            Configuration = new AdvancedAssistantConfiguration { Schema = ModelProviderSchema.OpenAI }
        };
        var assistantNotifications = new List<string?>();
        assistant.PropertyChanged += (_, args) => assistantNotifications.Add(args.PropertyName);

        assistant.OpenAIOptions.ReasoningEffort = "low|high";
        Assert.That(assistantNotifications, Does.Not.Contain(nameof(Assistant.EffectiveReasoningOptions)));

        assistant.Configuration.Schema = ModelProviderSchema.Google;
        Assert.Multiple(() =>
        {
            Assert.That(assistant.EffectiveReasoningOptions, Is.SameAs(assistant.GoogleOptions));
            Assert.That(assistantNotifications, Does.Contain(nameof(Assistant.EffectiveSchemaOptions)));
            Assert.That(assistantNotifications, Does.Contain(nameof(Assistant.EffectiveReasoningOptions)));
        });
    }

    [Test]
    public void SnapshotMapper_WhenOptionsChange_KeepsSelectionAndCatalogDefaultsDetached()
    {
        var source = new OpenAIOptions
        {
            SelectedReasoningEffort = "high",
            ThinkingType = "enabled",
            Temperature = "0.5",
            DefaultReasoningEffortValues = ["low", "high"],
        };

        var snapshot = AssistantSnapshotMapper.Copy(source);
        source.SelectedReasoningEffort = "low";
        source.DefaultReasoningEffortValues = ["low", "medium", "high"];

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.SelectedReasoningEffort, Is.EqualTo("high"));
            Assert.That(snapshot.DefaultReasoningEffortValues, Is.EqualTo(new[] { "low", "high" }));
            Assert.That(snapshot.EffectiveReasoningEffort, Is.EqualTo("high"));
            Assert.That(snapshot.ThinkingType, Is.EqualTo("enabled"));
            Assert.That(snapshot.Temperature, Is.EqualTo("0.5"));
        });
    }

    [Test]
    public void Serialization_WhenSavingOptions_ExcludesTransientAndEffectiveReasoningState()
    {
        var options = new OpenAIOptions
        {
            ReasoningEffort = "low|high",
            SelectedReasoningEffort = "high",
            DefaultReasoningEffortValues = ["minimal", "low", "high"],
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(options));
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty(nameof(ReasoningModelSchemaOptions.SelectedReasoningEffort)).GetString(), Is.EqualTo("high"));
            Assert.That(json.RootElement.TryGetProperty(nameof(ReasoningModelSchemaOptions.DefaultReasoningEffortValues), out _), Is.False);
            Assert.That(json.RootElement.TryGetProperty(nameof(ReasoningModelSchemaOptions.ReasoningEffortValues), out _), Is.False);
            Assert.That(json.RootElement.TryGetProperty(nameof(ReasoningModelSchemaOptions.EffectiveReasoningEffort), out _), Is.False);
        });
    }

    [Test]
    public void ModelDefinitionTemplate_MessagePackRoundTrip_PreservesReasoningEffortValues()
    {
        var source = new ModelDefinitionTemplate
        {
            ModelId = "model",
            Name = "Model",
            SupportsToolCall = true,
            InputModalities = Modalities.Text,
            OutputModalities = Modalities.Text,
            ContextLimit = 1000,
            OutputLimit = 100,
            ReasoningEffortValues = ["low", "medium", "high"]
        };

        var bytes = MessagePackSerializer.Serialize(source);
        var result = MessagePackSerializer.Deserialize<ModelDefinitionTemplate>(bytes);

        Assert.That(result.ReasoningEffortValues, Is.EqualTo(source.ReasoningEffortValues));
    }
}
