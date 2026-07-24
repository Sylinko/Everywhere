using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.I18N;
using Everywhere.Skills;
using Everywhere.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Core.Tests.AI.Prompts;

public sealed class PromptPageViewModelTests
{
    [Test]
    public async Task ViewLoaded_ListsDefaultPromptFirstWithoutSelectingPrompt()
    {
        var userPrompt = UserPrompt("User template", "User prompt");
        var viewModel = CreateViewModel([new DefaultPromptProvider().DefaultPrompt, userPrompt]);

        await viewModel.ViewLoaded(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.FilteredPrompts, Has.Count.EqualTo(2));
            Assert.That(viewModel.FilteredPrompts[0].Id, Is.EqualTo(Guid.Empty));
            Assert.That(viewModel.FilteredPrompts[0].IsDefault, Is.True);
            Assert.That(viewModel.SelectedPromptItem, Is.Null);
        });
    }

    [Test]
    public async Task OnNavigatedTo_PendingRouteSelectsPromptAfterLoad()
    {
        var prompt = UserPrompt("Route target");
        var viewModel = CreateViewModel([new DefaultPromptProvider().DefaultPrompt, prompt]);

        viewModel.OnNavigatedTo([prompt.Id.ToString("D")]);
        await viewModel.ViewLoaded(CancellationToken.None);

        Assert.That(viewModel.SelectedPromptItem?.Id, Is.EqualTo(prompt.Id));
    }

    [Test]
    public async Task SelectedPrompt_RendersDefaultPromptAndSkillsPromptPlaceholders()
    {
        var prompt = UserPrompt("{DefaultSystemPrompt}\n\nTail");
        var skillPromptProvider = new TestSkillPromptProvider("Skill instructions");
        var viewModel = CreateViewModel(
            [new DefaultPromptProvider().DefaultPrompt, prompt],
            skillPromptProvider: skillPromptProvider);
        await viewModel.ViewLoaded(CancellationToken.None);

        viewModel.OnNavigatedTo([prompt.Id.ToString("D")]);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.RenderedPreview, Does.Contain("Skill instructions"));
            Assert.That(viewModel.RenderedPreview, Does.Contain("Tail"));
            Assert.That(viewModel.RenderedPreview, Does.Not.Contain("{SkillsPrompt}"));
        });
    }

    private static PromptPageViewModel CreateViewModel(
        IReadOnlyList<PromptDefinition> prompts,
        IAssistantPromptReferenceService? referenceService = null,
        ISkillPromptProvider? skillPromptProvider = null)
    {
        EnsureLocaleManager();
        return new PromptPageViewModel(
            new TestPromptService(prompts),
            referenceService ?? new TestAssistantPromptReferenceService([]),
            skillPromptProvider ?? new TestSkillPromptProvider(string.Empty),
            new ServiceCollection().BuildServiceProvider());
    }

    private static void EnsureLocaleManager()
    {
        try
        {
            _ = LocaleManager.Shared;
        }
        catch (InvalidOperationException)
        {
            _ = new LocaleManager();
        }
    }

    private static PromptDefinition UserPrompt(
        string template,
        string? name = null,
        PromptSource source = PromptSource.Blank) =>
        new(
            Guid.CreateVersion7(),
            name,
            template,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            source);

    private sealed class TestPromptService(IReadOnlyList<PromptDefinition> prompts) : IPromptService
    {
        public PromptDefinition DefaultPrompt => prompts.First(static prompt => prompt.IsDefault);

        public Task<IReadOnlyList<PromptDefinition>> ListPromptsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(prompts);

        public Task<IReadOnlyList<PromptDefinition>> ListUserPromptsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PromptDefinition?> GetPromptAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PromptDefinition> ResolvePromptAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(prompts.FirstOrDefault(prompt => prompt.Id == id) ?? DefaultPrompt);

        public Task<PromptDefinition> CreatePromptAsync(PromptCreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PromptDefinition?> UpdatePromptAsync(Guid id, PromptUpdateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeletePromptAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestAssistantPromptReferenceService(IReadOnlyList<AssistantPromptReference> references)
        : IAssistantPromptReferenceService
    {
        public IReadOnlyList<AssistantPromptReference> ListReferences(Guid promptId) =>
            references.Where(reference => reference.SystemPromptId == promptId).ToList();

        public Task<IReadOnlyList<UnresolvedAssistantPromptReference>> ListUnresolvedReferencesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnresolvedAssistantPromptReference>>([]);

        public int ResetReferencesToDefault(Guid promptId) => 0;
    }

    private sealed class TestSkillPromptProvider(string prompt) : ISkillPromptProvider
    {
        public string GetPrompt(ToolCallStatus toolCallStatus) => prompt;
    }
}
