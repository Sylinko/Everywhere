using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.AI.Prompts.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.AI.Prompts;

public sealed class PromptServiceTests
{
    [Test]
    public async Task PromptDbInitializer_MigratesPromptDatabase()
    {
        using var database = PromptTestDatabase.Create();
        var initializer = new PromptDbInitializer(database.Factory, NullLogger<PromptDbInitializer>.Instance);

        await initializer.InitializeAsync();

        await using var dbContext = await database.Factory.CreateDbContextAsync();
        Assert.That(await dbContext.Prompts.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task ListPromptsAsync_ReturnsVirtualDefaultPromptBeforeUserPrompts()
    {
        using var database = PromptTestDatabase.Create();
        await database.MigrateAsync();
        var service = CreateService(database);

        var userPrompt = await service.CreatePromptAsync(new PromptCreateRequest("User template", "User prompt"));

        var prompts = await service.ListPromptsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(prompts, Has.Count.EqualTo(2));
            Assert.That(prompts[0].Id, Is.EqualTo(Guid.Empty));
            Assert.That(prompts[0].IsBuiltIn, Is.True);
            Assert.That(prompts[0].Template, Is.EqualTo(DefaultPrompts.DefaultSystemPrompt));
            Assert.That(prompts[1].Id, Is.EqualTo(userPrompt.Id));
        });
    }

    [Test]
    public async Task CreatePromptAsync_PersistsUserPrompt()
    {
        using var database = PromptTestDatabase.Create();
        await database.MigrateAsync();
        var service = CreateService(database);
        var id = Guid.CreateVersion7();

        var prompt = await service.CreatePromptAsync(
            new PromptCreateRequest(
                "Template",
                "  Name  ",
                Id: id));

        var saved = await service.GetPromptAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(prompt.Id, Is.EqualTo(id));
            Assert.That(prompt.Name, Is.EqualTo("Name"));
            Assert.That(prompt.Template, Is.EqualTo("Template"));
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.Template, Is.EqualTo("Template"));
        });
    }

    [Test]
    public async Task UpdatePromptAsync_UpdatesExistingUserPrompt()
    {
        using var database = PromptTestDatabase.Create();
        await database.MigrateAsync();
        var service = CreateService(database);
        var prompt = await service.CreatePromptAsync(new PromptCreateRequest("Old", "Old name", PromptSource.Blank));

        var updated = await service.UpdatePromptAsync(
            prompt.Id,
            new PromptUpdateRequest(
                "New",
                "New name"));

        Assert.Multiple(() =>
        {
            Assert.That(updated, Is.Not.Null);
            Assert.That(updated!.Id, Is.EqualTo(prompt.Id));
            Assert.That(updated.Name, Is.EqualTo("New name"));
            Assert.That(updated.Template, Is.EqualTo("New"));
        });
    }

    [Test]
    public async Task DeletePromptAsync_RemovesUserPromptButNotDefaultPrompt()
    {
        using var database = PromptTestDatabase.Create();
        await database.MigrateAsync();
        var service = CreateService(database);
        var prompt = await service.CreatePromptAsync(new PromptCreateRequest("Template"));

        var deletedDefault = await service.DeletePromptAsync(Guid.Empty);
        var deletedUser = await service.DeletePromptAsync(prompt.Id);
        var missing = await service.GetPromptAsync(prompt.Id);

        Assert.Multiple(() =>
        {
            Assert.That(deletedDefault, Is.False);
            Assert.That(deletedUser, Is.True);
            Assert.That(missing, Is.Null);
        });
    }

    [Test]
    public void CreatePromptAsync_RejectsDefaultPromptId()
    {
        using var database = PromptTestDatabase.Create();
        var service = CreateService(database);

        Assert.ThrowsAsync<ArgumentException>(
            async () => await service.CreatePromptAsync(
                new PromptCreateRequest("Template", Id: Guid.Empty)));
    }

    [Test]
    public void CreatePromptAsync_RejectsEmptyTemplate()
    {
        using var database = PromptTestDatabase.Create();
        var service = CreateService(database);

        Assert.ThrowsAsync<ArgumentException>(
            async () => await service.CreatePromptAsync(new PromptCreateRequest("   ")));
    }

    private static PromptService CreateService(PromptTestDatabase database) =>
        new(database.Factory, new DefaultPromptProvider());
}
