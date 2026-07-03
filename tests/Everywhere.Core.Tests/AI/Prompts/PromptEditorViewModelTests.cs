using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI.Prompts;
using Everywhere.Messages;
using Everywhere.Skills;
using Everywhere.ViewModels;
using MessagePack;

namespace Everywhere.Core.Tests.AI.Prompts;

public sealed class PromptEditorViewModelTests
{
    [Test]
    public async Task SaveCommand_CreateModeCreatesPromptAndNavigatesBackToPrompt()
    {
        var promptService = new TestPromptService();
        var viewModel = new PromptEditorViewModel(promptService, new TestSkillPromptProvider());
        using var navigation = new NavigationCapture();

        viewModel.OpenForCreate();
        viewModel.Name = "Created prompt";
        viewModel.Template = "{DefaultSystemPrompt}\n\nCreated body";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(promptService.UserPrompts, Has.Count.EqualTo(1));
            Assert.That(promptService.UserPrompts[0].Name, Is.EqualTo("Created prompt"));
            Assert.That(promptService.UserPrompts[0].Template, Does.Contain("Created body"));
            Assert.That(navigation.Routes.LastOrDefault(), Is.EqualTo(MainViewNavigateMessage.ToPrompt(promptService.UserPrompts[0].Id)));
        });
    }

    [Test]
    public async Task SaveCommand_EditModeUpdatesPromptAndDetachesGuidedMetadata()
    {
        var snapshot = new PromptRecipeSnapshot
        {
            SchemaVersion = 1,
            PersonaId = "general",
            IsDetachedFromRecipe = false
        };
        var metadata = MessagePackSerializer.Serialize(snapshot);
        var prompt = UserPrompt(
            "Old template",
            "Old name",
            PromptSource.Guided,
            metadata);
        var promptService = new TestPromptService(prompt);
        var viewModel = new PromptEditorViewModel(promptService, new TestSkillPromptProvider());
        using var navigation = new NavigationCapture();

        Assert.That(await viewModel.OpenForEditAsync(prompt.Id), Is.True);
        viewModel.Name = "Updated name";
        viewModel.Template = "{DefaultSystemPrompt}\n\nUpdated template";
        await viewModel.SaveCommand.ExecuteAsync(null);

        var updatedSnapshot = MessagePackSerializer.Deserialize<PromptRecipeSnapshot>(
            promptService.UserPrompts[0].MetadataPayload!);

        Assert.Multiple(() =>
        {
            Assert.That(promptService.UserPrompts[0].Name, Is.EqualTo("Updated name"));
            Assert.That(promptService.UserPrompts[0].Template, Does.Contain("Updated template"));
            Assert.That(updatedSnapshot.IsDetachedFromRecipe, Is.True);
            Assert.That(navigation.Routes.LastOrDefault(), Is.EqualTo(MainViewNavigateMessage.ToPrompt(prompt.Id)));
        });
    }

    [Test]
    public void CanSave_EmptyTemplateIsFalse()
    {
        var viewModel = new PromptEditorViewModel(new TestPromptService(), new TestSkillPromptProvider());

        viewModel.OpenForCreate();
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
        var viewModel = new PromptEditorViewModel(new TestPromptService(prompt), new TestSkillPromptProvider());
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
            metadataPayload);

    private sealed class TestPromptService(params PromptDefinition[] prompts) : IPromptService
    {
        private readonly PromptDefinition _defaultPrompt = new DefaultPromptProvider().DefaultPrompt;
        private readonly List<PromptDefinition> _userPrompts = prompts.Where(static prompt => !prompt.IsDefault).ToList();

        public IReadOnlyList<PromptDefinition> UserPrompts => _userPrompts;

        public PromptDefinition DefaultPrompt => prompts.FirstOrDefault(static prompt => prompt.IsDefault) ?? _defaultPrompt;

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
        public string GetPrompt() => "Skill prompt";
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
