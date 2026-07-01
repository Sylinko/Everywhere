using Everywhere.AI;
using Everywhere.AI.Configurator;
using Everywhere.Views;

namespace Everywhere.Core.Tests.AI;

[TestFixture]
public class OfficialModelDefinitionTests
{
    [Test]
    public void Reconcile_EmptyCloudList_KeepsCurrentSelectionSnapshot()
    {
        var assistant = CreateAssistant("old-model");
        assistant.ContextLimit = 4096;
        assistant.DeprecationDate = new DateOnly(2026, 6, 1);

        var result = OfficialModelDefinitionForm.Reconcile(assistant, assistant.ModelId, null, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSelectedModelUnavailable, Is.False);
            Assert.That(result.SelectedItem?.ModelId, Is.EqualTo("old-model"));
            Assert.That(result.SelectedItem?.ContextLimit, Is.EqualTo(4096));
            Assert.That(result.SelectedItem?.DeprecationDate, Is.EqualTo(new DateOnly(2026, 6, 1)));
        }
    }

    [Test]
    public void Reconcile_CurrentModelInCloudList_UsesLatestCloudDefinition()
    {
        var assistant = CreateAssistant("model-a");
        assistant.ContextLimit = 1000;
        assistant.SupportsToolCall = false;
        var latestModel = CreateModel("model-a", contextLimit: 2000, supportsToolCall: true);

        var result = OfficialModelDefinitionForm.Reconcile(
            assistant,
            assistant.ModelId,
            null,
            [latestModel]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSelectedModelUnavailable, Is.False);
            Assert.That(result.SelectedItem, Is.SameAs(latestModel));
            Assert.That(result.SelectedItem?.ContextLimit, Is.EqualTo(2000));
            Assert.That(result.SelectedItem?.SupportsToolCall, Is.True);
        }
    }

    [Test]
    public void Reconcile_CurrentModelMissingFromNonEmptyCloudList_KeepsSelectionAndMarksUnavailable()
    {
        var assistant = CreateAssistant("old-model");
        var replacement = CreateModel("new-model");

        var result = OfficialModelDefinitionForm.Reconcile(
            assistant,
            assistant.ModelId,
            null,
            [replacement]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSelectedModelUnavailable, Is.True);
            Assert.That(result.SelectedItem?.ModelId, Is.EqualTo("old-model"));
            Assert.That(result.Items.Select(x => x.ModelId), Is.EqualTo(["old-model", "new-model"]));
        }
    }

    [Test]
    public void Reconcile_TemporaryNullSelection_StillReselectsByTargetModelId()
    {
        var assistant = CreateAssistant("model-a");
        var latestModel = CreateModel("model-a", contextLimit: 3000);

        var result = OfficialModelDefinitionForm.Reconcile(
            assistant,
            "model-a",
            null,
            [latestModel]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TargetModelId, Is.EqualTo("model-a"));
            Assert.That(result.SelectedItem, Is.SameAs(latestModel));
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

        assistant.ApplyTemplate(template);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assistant.ModelId, Is.EqualTo("model-a"));
            Assert.That(assistant.SupportsToolCall, Is.True);
            Assert.That(assistant.ContextLimit, Is.EqualTo(1234));
            Assert.That(assistant.OutputLimit, Is.EqualTo(567));
            Assert.That(assistant.Specializations, Is.EqualTo(ModelSpecializations.TitleGeneration));
            Assert.That(assistant.DeprecationDate, Is.EqualTo(new DateOnly(2026, 6, 1)));
        }
    }

    [Test]
    public void Availability_EmptyCloudList_DoesNotMarkUnavailable()
    {
        var today = new DateOnly(2026, 5, 20);
        var assistant = CreateAssistant("model-a");

        var availability = ModelAvailability.Evaluate(
            assistant,
            [],
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
            assistant,
            [CreateModel("other-model")],
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
            assistant,
            [CreateModel("model-a", deprecationDate: today.AddDays(8))],
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
            assistant,
            [CreateModel("model-a", deprecationDate: today.AddDays(7))],
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
            assistant,
            [CreateModel("model-a", deprecationDate: today)],
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
            CreateAssistant("preset-model", today.AddDays(7)),
            [],
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
            ConfiguratorType = AssistantConfiguratorType.Official,
            ModelId = modelId,
            SupportsToolCall = false,
            InputModalities = Modalities.Text,
            OutputModalities = Modalities.Text,
            ContextLimit = 1000,
            OutputLimit = 100,
            Specializations = ModelSpecializations.Default,
            DeprecationDate = deprecationDate
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
}