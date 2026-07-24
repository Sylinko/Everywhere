using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Messages;
using Everywhere.Skills;
using Everywhere.ViewModels;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.AI.Prompts;

public sealed class PromptEditorViewModelTests
{
    [Test]
    public void ComposeTemplate_IncludesDefaultPromptAndSelectedRecipeSections()
    {
        var snapshot = PromptRecipeCatalog.CreateDefaultSnapshot();
        snapshot.PersonaId = "programming";
        snapshot.ScenarioIds = ["programming-development", "code-review"];
        snapshot.ToneId = "concise-direct";
        snapshot.DetailLevelId = "detailed";
        snapshot.OrganizationId = "step-by-step";
        snapshot.PreferredUserName = "DearVa";
        snapshot.AdditionalRequirements = "Prefer concrete examples.";

        var template = PromptRecipeCatalog.ComposeTemplate(snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(template, Does.Contain("{DefaultSystemPrompt}"));
            Assert.That(template, Does.Contain("programming assistant"));
            Assert.That(template, Does.Contain("Programming and development"));
            Assert.That(template, Does.Contain("Code review"));
            Assert.That(template, Does.Contain("Be concise and direct."));
            Assert.That(template, Does.Contain("Use detailed explanations"));
            Assert.That(template, Does.Contain("Use step-by-step"));
            Assert.That(template, Does.Contain("DearVa"));
            Assert.That(template, Does.Contain("Prefer concrete examples."));
        });
    }

    [Test]
    public void NormalizeSnapshot_LimitsScenariosToThree()
    {
        var snapshot = PromptRecipeCatalog.CreateDefaultSnapshot();
        snapshot.ScenarioIds =
        [
            "general-qa",
            "programming-development",
            "code-review",
            "writing-editing"
        ];

        var normalized = PromptRecipeCatalog.NormalizeSnapshot(snapshot, detached: false);

        Assert.That(normalized.ScenarioIds, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task SaveCommand_NewQuickPromptCreatesGuidedPrompt()
    {
        var promptService = new TestPromptService();
        var viewModel = CreateViewModel(promptService);
        using var navigation = new NavigationCapture();

        viewModel.OpenForCreate();
        viewModel.Name = "Guided prompt";
        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = promptService.UserPrompts.Single();
        var snapshot = MessagePackSerializer.Deserialize<PromptRecipeSnapshot>(saved.MetadataPayload!);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsQuickMode, Is.True);
            Assert.That(saved.Name, Is.EqualTo("Guided prompt"));
            Assert.That(saved.Source, Is.EqualTo(PromptSource.Guided));
            Assert.That(saved.Template, Does.Contain("{DefaultSystemPrompt}"));
            Assert.That(snapshot.IsDetachedFromRecipe, Is.False);
            Assert.That(navigation.Routes.LastOrDefault(), Is.EqualTo(MainViewNavigateMessage.ToPrompt(saved.Id)));
        });
    }

    [Test]
    public async Task SaveCommand_NewAdvancedPromptDetachesGuidedMetadata()
    {
        var promptService = new TestPromptService();
        var viewModel = CreateViewModel(promptService);
        using var navigation = new NavigationCapture();

        viewModel.OpenForCreate();
        viewModel.SwitchToAdvancedCommand.Execute(null);
        viewModel.Template += "\n\n# Manual Edit\nUse short examples.";
        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = promptService.UserPrompts.Single();
        var snapshot = MessagePackSerializer.Deserialize<PromptRecipeSnapshot>(saved.MetadataPayload!);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsAdvancedMode, Is.True);
            Assert.That(saved.Source, Is.EqualTo(PromptSource.Guided));
            Assert.That(saved.Template, Does.Contain("Manual Edit"));
            Assert.That(snapshot.IsDetachedFromRecipe, Is.True);
            Assert.That(navigation.Routes.LastOrDefault(), Is.EqualTo(MainViewNavigateMessage.ToPrompt(saved.Id)));
        });
    }

    [Test]
    public async Task OpenForEdit_GuidedAttachedPromptUsesQuickModeAndSavesAttached()
    {
        var snapshot = PromptRecipeCatalog.CreateDefaultSnapshot();
        snapshot.PersonaId = "programming";
        var metadata = MessagePackSerializer.Serialize(PromptRecipeCatalog.NormalizeSnapshot(snapshot, detached: false));
        var prompt = UserPrompt(
            PromptRecipeCatalog.ComposeTemplate(snapshot),
            "Old name",
            PromptSource.Guided,
            metadata);
        var promptService = new TestPromptService(prompt);
        var viewModel = CreateViewModel(promptService);

        Assert.That(await viewModel.OpenForEditAsync(prompt.Id), Is.True);
        viewModel.Name = "Updated name";
        await viewModel.SaveCommand.ExecuteAsync(null);

        var savedSnapshot = MessagePackSerializer.Deserialize<PromptRecipeSnapshot>(
            promptService.UserPrompts[0].MetadataPayload!);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsQuickMode, Is.True);
            Assert.That(promptService.UserPrompts[0].Name, Is.EqualTo("Updated name"));
            Assert.That(promptService.UserPrompts[0].Source, Is.EqualTo(PromptSource.Guided));
            Assert.That(savedSnapshot.PersonaId, Is.EqualTo("programming"));
            Assert.That(savedSnapshot.IsDetachedFromRecipe, Is.False);
        });
    }

    [Test]
    public async Task OpenForEdit_GuidedDetachedPromptStartsAdvancedAndCanReturnToQuick()
    {
        var snapshot = PromptRecipeCatalog.CreateDefaultSnapshot();
        var detachedSnapshot = PromptRecipeCatalog.NormalizeSnapshot(snapshot, detached: true);
        var prompt = UserPrompt(
            PromptRecipeCatalog.ComposeTemplate(snapshot),
            "Detached",
            PromptSource.Guided,
            MessagePackSerializer.Serialize(detachedSnapshot));
        var promptService = new TestPromptService(prompt);
        var viewModel = CreateViewModel(promptService);

        Assert.That(await viewModel.OpenForEditAsync(prompt.Id), Is.True);
        Assert.That(viewModel.IsAdvancedMode, Is.True);

        await viewModel.SwitchToQuickCommand.ExecuteAsync(null);
        await viewModel.SaveCommand.ExecuteAsync(null);

        var savedSnapshot = MessagePackSerializer.Deserialize<PromptRecipeSnapshot>(
            promptService.UserPrompts[0].MetadataPayload!);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsQuickMode, Is.True);
            Assert.That(promptService.UserPrompts[0].Source, Is.EqualTo(PromptSource.Guided));
            Assert.That(savedSnapshot.IsDetachedFromRecipe, Is.False);
        });
    }

    [Test]
    public async Task SaveCommand_NonGuidedAdvancedEditPreservesOriginalSourceAndMetadata()
    {
        var metadata = new byte[] { 1, 2, 3 };
        var prompt = UserPrompt(
            "Original template",
            "Imported",
            PromptSource.Import,
            metadata);
        var promptService = new TestPromptService(prompt);
        var viewModel = CreateViewModel(promptService);

        Assert.That(await viewModel.OpenForEditAsync(prompt.Id), Is.True);
        viewModel.Template = "Updated imported template";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsAdvancedMode, Is.True);
            Assert.That(promptService.UserPrompts[0].Template, Is.EqualTo("Updated imported template"));
            Assert.That(promptService.UserPrompts[0].Source, Is.EqualTo(PromptSource.Import));
            Assert.That(promptService.UserPrompts[0].MetadataPayload, Is.EqualTo(metadata));
        });
    }

    [Test]
    public void CanSave_EmptyTemplateIsFalse()
    {
        var viewModel = CreateViewModel(new TestPromptService());

        viewModel.OpenForCreate();
        viewModel.SwitchToAdvancedCommand.Execute(null);
        viewModel.Template = "   ";

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.CanSave, Is.False);
            Assert.That(viewModel.SaveCommand.CanExecute(null), Is.False);
        });
    }

    [Test]
    public async Task CancelCommand_ReturnsToOriginalPromptWhenEditing()
    {
        var prompt = UserPrompt("Template", "Name");
        var viewModel = CreateViewModel(new TestPromptService(prompt));
        using var navigation = new NavigationCapture();

        Assert.That(await viewModel.OpenForEditAsync(prompt.Id), Is.True);
        viewModel.CancelCommand.Execute(null);

        Assert.That(navigation.Routes.LastOrDefault(), Is.EqualTo(MainViewNavigateMessage.ToPrompt(prompt.Id)));
    }

    private static PromptDefinition UserPrompt(
        string template,
        string? name = null,
        PromptSource source = PromptSource.Blank,
        byte[]? metadataPayload = null) =>
        new(
            Guid.CreateVersion7(),
            name,
            template,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            source,
            metadataPayload?.ToArray());

    private static PromptEditorViewModel CreateViewModel(TestPromptService promptService) =>
        new(
            promptService,
            new TestSkillPromptProvider(),
            NullLogger<PromptEditorViewModel>.Instance);

    private sealed class TestPromptService(params PromptDefinition[] prompts) : IPromptService
    {
        private readonly List<PromptDefinition> _userPrompts = prompts.Where(static prompt => !prompt.IsDefault).ToList();

        public IReadOnlyList<PromptDefinition> UserPrompts => _userPrompts;

        public PromptDefinition DefaultPrompt
        {
            get => prompts.FirstOrDefault(static prompt => prompt.IsDefault) ?? field;
        } = new DefaultPromptProvider().DefaultPrompt;

        public Task<IReadOnlyList<PromptDefinition>> ListPromptsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PromptDefinition>>([DefaultPrompt, .. _userPrompts]);

        public Task<IReadOnlyList<PromptDefinition>> ListUserPromptsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PromptDefinition>>(_userPrompts);

        public Task<PromptDefinition?> GetPromptAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (id == Guid.Empty)
            {
                return Task.FromResult<PromptDefinition?>(DefaultPrompt);
            }

            return Task.FromResult(_userPrompts.FirstOrDefault(prompt => prompt.Id == id));
        }

        public Task<PromptDefinition> CreatePromptAsync(
            PromptCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            var prompt = new PromptDefinition(
                request.Id ?? Guid.CreateVersion7(),
                string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
                request.Template,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                request.Source,
                request.MetadataPayload?.ToArray());
            _userPrompts.Add(prompt);
            return Task.FromResult(prompt);
        }

        public Task<PromptDefinition?> UpdatePromptAsync(
            Guid id,
            PromptUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            var index = _userPrompts.FindIndex(prompt => prompt.Id == id);
            if (index < 0)
            {
                return Task.FromResult<PromptDefinition?>(null);
            }

            var oldPrompt = _userPrompts[index];
            var updated = oldPrompt with
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
                Template = request.Template,
                UpdatedAt = DateTimeOffset.UtcNow,
                Source = request.Source ?? oldPrompt.Source,
                MetadataPayload = request.MetadataPayload?.ToArray()
            };
            _userPrompts[index] = updated;
            return Task.FromResult<PromptDefinition?>(updated);
        }

        public Task<bool> DeletePromptAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestSkillPromptProvider : ISkillPromptProvider
    {
        public string GetPrompt(ToolCallStatus toolCallStatus) => "Skill prompt";
    }

    private sealed class NavigationCapture : IDisposable
    {
        public List<object> Routes { get; } = [];

        public NavigationCapture()
        {
            WeakReferenceMessenger.Default.Register<MainViewNavigateMessage>(
                this,
                static (recipient, message) => ((NavigationCapture)recipient).Routes.Add(message.Route));
        }

        public void Dispose()
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }
    }
}
