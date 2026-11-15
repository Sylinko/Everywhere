using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Configuration;
using Everywhere.Skills;
using Everywhere.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ShadUI;

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

    [AvaloniaTest]
    public async Task DeletePrompt_WhenDatabaseCompletes_ResetsOnlyCurrentReferencesOnSuccess()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(provider);
        var prompt = UserPrompt("To delete");
        var assistant = new CustomAssistant { SystemPromptId = prompt.Id };
        settings.Model.CustomAssistants.Add(assistant);
        var prompts = Substitute.For<IPromptService>();
        prompts.ListPromptsAsync().Returns([new DefaultPromptProvider().DefaultPrompt, prompt]);
        var firstAttempt = new TaskCompletionSource<bool>();
        var retry = new TaskCompletionSource<bool>();
        prompts.DeletePromptAsync(prompt.Id, Arg.Any<CancellationToken>()).Returns(firstAttempt.Task, retry.Task);
        using var model = new PromptPageViewModel(prompts, settings, new TestSkillPromptProvider(""), provider);
        var host = new DialogHost();
        var window = new Window { Content = host };
        try
        {
            window.Show();
            await model.ViewLoaded(CancellationToken.None);
            model.OnNavigatedTo([prompt.Id.ToString()]);

            var pending = model.DeleteSelectedPromptCommand.ExecuteAsync(null);
            host.CloseAll(DialogResult.Primary);
            firstAttempt.SetResult(false);
            await pending;
            Assert.That(assistant.SystemPromptId, Is.EqualTo(prompt.Id));

            pending = model.DeleteSelectedPromptCommand.ExecuteAsync(null);
            host.CloseAll(DialogResult.Primary);
            var otherId = Guid.NewGuid();
            assistant.SystemPromptId = otherId;
            var lateReference = new CustomAssistant { SystemPromptId = prompt.Id };
            settings.Model.CustomAssistants.Add(lateReference);
            retry.SetResult(true);
            await pending;
            Assert.That(assistant.SystemPromptId, Is.EqualTo(otherId));
            Assert.That(lateReference.SystemPromptId, Is.EqualTo(Guid.Empty));
        }
        finally
        {
            await model.ViewUnloaded();
            window.Close();
        }
    }

    private static PromptPageViewModel CreateViewModel(
        IReadOnlyList<PromptDefinition> prompts,
        ISkillPromptProvider? skillPromptProvider = null)
    {
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        return new PromptPageViewModel(
            new TestPromptService(prompts),
            new Settings(serviceProvider),
            skillPromptProvider ?? new TestSkillPromptProvider(string.Empty),
            serviceProvider);
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

    private sealed class TestSkillPromptProvider(string prompt) : ISkillPromptProvider
    {
        public string GetPrompt(ToolCallStatus toolCallStatus) => prompt;
    }
}
