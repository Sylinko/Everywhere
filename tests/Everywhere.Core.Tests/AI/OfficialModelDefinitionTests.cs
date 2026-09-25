using Everywhere.AI;
using Everywhere.Cloud;
using NSubstitute;

namespace Everywhere.Core.Tests.AI;

[TestFixture]
public class OfficialModelDefinitionTests
{
    [Test]
    public void Reconcile_EmptyCloudList_KeepsCurrentSelectionSnapshot()
    {
        var assistant = CreateAssistant("old-model");
        assistant.Configuration.ContextLimit = 4096;
        assistant.Configuration.DeprecationDate = new DateOnly(2026, 6, 1);

        var result = Resolve((OfficialAssistantConfiguration)assistant.Configuration, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSelectedModelUnavailable, Is.False);
            Assert.That(result.SelectedItem?.ModelId, Is.EqualTo("old-model"));
            Assert.That(result.SelectedItem?.Model.ContextLimit, Is.EqualTo(4096));
            Assert.That(result.SelectedItem?.Model.DeprecationDate, Is.EqualTo(new DateOnly(2026, 6, 1)));
        }
    }

    [Test]
    public void Reconcile_CurrentModelInCloudList_UsesLatestCloudDefinition()
    {
        var assistant = CreateAssistant("model-a");
        assistant.Configuration.ContextLimit = 1000;
        assistant.Configuration.SupportsToolCall = false;
        var latestModel = CreateModel("model-a", contextLimit: 2000, supportsToolCall: true);

        var result = Resolve(
            (OfficialAssistantConfiguration)assistant.Configuration,
            false,
            CreateDefinition(latestModel));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSelectedModelUnavailable, Is.False);
            Assert.That(result.SelectedItem?.Model, Is.SameAs(latestModel));
            Assert.That(result.SelectedItem?.Model.ContextLimit, Is.EqualTo(2000));
            Assert.That(result.SelectedItem?.Model.SupportsToolCall, Is.True);
        }
    }

    [Test]
    public void Reconcile_CurrentModelMissingFromNonEmptyCloudList_KeepsSelectionAndMarksUnavailable()
    {
        var assistant = CreateAssistant("old-model");
        var replacement = CreateModel("new-model");

        var result = Resolve(
            (OfficialAssistantConfiguration)assistant.Configuration,
            true,
            CreateDefinition(replacement));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSelectedModelUnavailable, Is.True);
            Assert.That(result.SelectedItem?.ModelId, Is.EqualTo("old-model"));
            Assert.That(result.Items.Select(static item => item.ModelId), Is.EqualTo(["old-model", "new-model"]));
        }
    }

    [Test]
    public void Reconcile_TemporaryNullSelection_StillReselectsByTargetModelId()
    {
        var assistant = CreateAssistant("model-a");
        var latestModel = CreateModel("model-a", contextLimit: 3000);

        var result = Resolve(
            (OfficialAssistantConfiguration)assistant.Configuration,
            false,
            CreateDefinition(latestModel));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SelectedItem?.ModelId, Is.EqualTo("model-a"));
            Assert.That(result.SelectedItem?.Model, Is.SameAs(latestModel));
        }
    }

    [Test]
    public void ApplyTemplate_SyncsModelCapabilitiesAndDeprecationDate()
    {
        var assistant = CreateAssistant("old-model");
        var template = CreateModel(
            "model-a",
            supportsToolCall: true,
            contextLimit: 1234,
            outputLimit: 567,
            specializations: ModelSpecializations.TitleGeneration,
            deprecationDate: new DateOnly(2026, 6, 1));

        assistant.Configuration.Apply(template);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assistant.Configuration.ModelId, Is.EqualTo("model-a"));
            Assert.That(assistant.Configuration.SupportsToolCall, Is.True);
            Assert.That(assistant.Configuration.ContextLimit, Is.EqualTo(1234));
            Assert.That(assistant.Configuration.OutputLimit, Is.EqualTo(567));
            Assert.That(assistant.Configuration.Specializations, Is.EqualTo(ModelSpecializations.TitleGeneration));
            Assert.That(assistant.Configuration.DeprecationDate, Is.EqualTo(new DateOnly(2026, 6, 1)));
        }
    }

    [Test]
    public void Availability_EmptyCloudList_DoesNotMarkUnavailable()
    {
        var today = new DateOnly(2026, 5, 20);
        var assistant = CreateAssistant("model-a");

        var availability = ModelAvailability.Evaluate(
            assistant.Configuration,
            null,
            false,
            today);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.Kind, Is.EqualTo(ModelAvailabilityKind.Unknown));
            Assert.That(availability.ShouldShowChatNotification, Is.False);
        }
    }

    [Test]
    public void Availability_NonEmptyCloudListMissingCurrentModel_MarksUnavailable()
    {
        var today = new DateOnly(2026, 5, 20);
        var assistant = CreateAssistant("missing-model");

        var availability = ModelAvailability.Evaluate(
            assistant.Configuration,
            null,
            true,
            today);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.Kind, Is.EqualTo(ModelAvailabilityKind.Unavailable));
            Assert.That(availability.ShouldShowChatNotification, Is.True);
        }
    }

    [Test]
    public void Availability_DeprecationAfterEightDays_DoesNotShowChatWarning()
    {
        var today = new DateOnly(2026, 5, 20);
        var assistant = CreateAssistant("model-a");

        var availability = ModelAvailability.Evaluate(
            assistant.Configuration,
            CreateModel("model-a", deprecationDate: today.AddDays(8)),
            true,
            today);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.Kind, Is.EqualTo(ModelAvailabilityKind.Available));
            Assert.That(availability.ShouldShowChatNotification, Is.False);
        }
    }

    [Test]
    public void Availability_DeprecationWithinSevenDays_ShowsWarning()
    {
        var today = new DateOnly(2026, 5, 20);
        var assistant = CreateAssistant("model-a");

        var availability = ModelAvailability.Evaluate(
            assistant.Configuration,
            CreateModel("model-a", deprecationDate: today.AddDays(7)),
            true,
            today);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.Kind, Is.EqualTo(ModelAvailabilityKind.DeprecatingSoon));
            Assert.That(availability.ShouldShowChatNotification, Is.True);
        }
    }

    [Test]
    public void Availability_ExpiredDeprecationDate_ShowsError()
    {
        var today = new DateOnly(2026, 5, 20);
        var assistant = CreateAssistant("model-a");

        var availability = ModelAvailability.Evaluate(
            assistant.Configuration,
            CreateModel("model-a", deprecationDate: today),
            true,
            today);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.Kind, Is.EqualTo(ModelAvailabilityKind.Deprecated));
            Assert.That(availability.ShouldShowChatNotification, Is.True);
        }
    }

    [Test]
    public void Availability_ModelSelectionWithKnownDeprecationDate_DoesNotRequireAssistant()
    {
        var today = new DateOnly(2026, 5, 20);

        var availability = ModelAvailability.Evaluate(
            CreateAssistant("preset-model", today.AddDays(7)).Configuration,
            null,
            false,
            today);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.Kind, Is.EqualTo(ModelAvailabilityKind.DeprecatingSoon));
            Assert.That(availability.ShouldShowChatNotification, Is.True);
        }
    }

    [Test]
    public void DismissalKey_ChangesByDayModelKindAndDeprecationDate()
    {
        var assistantId = Guid.CreateVersion7();
        var today = new DateOnly(2026, 5, 20);
        var baseAvailability = new ModelAvailability(
            ModelAvailabilityKind.DeprecatingSoon,
            "model-a",
            new DateOnly(2026, 5, 27));

        var key = baseAvailability.CreateDismissalKey(assistantId, today);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baseAvailability.CreateDismissalKey(assistantId, today), Is.EqualTo(key));
            Assert.That(baseAvailability.CreateDismissalKey(assistantId, today.AddDays(1)), Is.Not.EqualTo(key));
            Assert.That(
                new ModelAvailability(ModelAvailabilityKind.DeprecatingSoon, "model-b", new DateOnly(2026, 5, 27)).CreateDismissalKey(
                    assistantId,
                    today),
                Is.Not.EqualTo(key));
            Assert.That(
                new ModelAvailability(ModelAvailabilityKind.Deprecated, "model-a", new DateOnly(2026, 5, 27)).CreateDismissalKey(
                    assistantId,
                    today),
                Is.Not.EqualTo(key));
            Assert.That(
                new ModelAvailability(ModelAvailabilityKind.DeprecatingSoon, "model-a", new DateOnly(2026, 5, 28)).CreateDismissalKey(
                    assistantId,
                    today),
                Is.Not.EqualTo(key));
        }
    }

    private static CustomAssistant CreateAssistant(string modelId, DateOnly? deprecationDate = null) =>
        new()
        {
            Configuration = new OfficialAssistantConfiguration
            {
                ModelId = modelId,
                SupportsToolCall = false,
                InputModalities = Modalities.Text,
                OutputModalities = Modalities.Text,
                ContextLimit = 1000,
                OutputLimit = 100,
                Specializations = ModelSpecializations.Default,
                DeprecationDate = deprecationDate
            }
        };

    private static ModelDefinitionTemplate CreateModel(
        string modelId,
        bool supportsToolCall = false,
        int contextLimit = 1000,
        int outputLimit = 100,
        ModelSpecializations specializations = ModelSpecializations.Default,
        DateOnly? deprecationDate = null) =>
        new()
        {
            ModelId = modelId,
            Name = modelId,
            SupportsToolCall = supportsToolCall,
            InputModalities = Modalities.Text,
            OutputModalities = Modalities.Text,
            ContextLimit = contextLimit,
            OutputLimit = outputLimit,
            Specializations = specializations,
            DeprecationDate = deprecationDate
        };

    private static OfficialModelDefinition CreateDefinition(ModelDefinitionTemplate model) =>
        new(model, ModelProviderSchema.OpenAI);

    private static AssistantCatalogSelection Resolve(
        OfficialAssistantConfiguration configuration,
        bool isAuthoritative,
        params OfficialModelDefinition[] definitions)
    {
        var preset = Substitute.For<IPresetModelProvider>();
        preset.Catalog.Returns(PresetModelCatalog.Empty);
        var official = Substitute.For<IOfficialModelProvider>();
        official.Catalog.Returns(new OfficialModelCatalog(
            ModelCatalogSnapshot<OfficialModelDefinition, string>.Create(
                definitions.Select(static definition =>
                    KeyValuePair.Create(definition.Model.ModelId, definition))),
            isAuthoritative));
        official.AccessStatus.Returns(OfficialModelCatalogAccessStatus.Available);
        return new AssistantCatalog(preset, official).Resolve(configuration);
    }
}
