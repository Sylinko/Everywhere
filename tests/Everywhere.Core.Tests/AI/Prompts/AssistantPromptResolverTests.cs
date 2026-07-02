using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.AI.PromptManager;

public sealed class AssistantPromptResolverTests
{
    [Test]
    public async Task ResolveSystemPromptAsync_DefaultPromptIdResolvesDefaultPrompt()
    {
        using var database = PromptTestDatabase.Create();
        var resolver = CreateResolver(database);
        var assistant = new CustomAssistant
        {
            SystemPromptId = PromptConstants.DefaultPromptId
        };

        var result = await resolver.ResolveSystemPromptAsync(assistant);

        Assert.Multiple(() =>
        {
            Assert.That(result.PromptId, Is.EqualTo(PromptConstants.DefaultPromptId));
            Assert.That(result.Template, Is.EqualTo(DefaultPrompts.DefaultSystemPrompt));
            Assert.That(result.UsedFallback, Is.False);
            Assert.That(result.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public async Task ResolveSystemPromptAsync_MissingPromptIdFallsBackWithDiagnostic()
    {
        using var database = PromptTestDatabase.Create();
        await database.MigrateAsync();
        var resolver = CreateResolver(database);
        var missingPromptId = Guid.CreateVersion7();
        var assistant = new CustomAssistant
        {
            Id = Guid.CreateVersion7(),
            SystemPromptId = missingPromptId
        };

        var result = await resolver.ResolveSystemPromptAsync(assistant);

        Assert.Multiple(() =>
        {
            Assert.That(result.PromptId, Is.EqualTo(PromptConstants.DefaultPromptId));
            Assert.That(result.Template, Is.EqualTo(DefaultPrompts.DefaultSystemPrompt));
            Assert.That(result.UsedFallback, Is.True);
            Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(PromptDiagnosticCode.UnresolvedReference));
        });
    }

    [Test]
    public async Task ResolveSystemPromptAsync_OverrideBypassesStoredPromptReference()
    {
        using var database = PromptTestDatabase.Create();
        var resolver = CreateResolver(database);
        var assistant = new CustomAssistant
        {
            SystemPromptId = Guid.CreateVersion7()
        };

        var result = await resolver.ResolveSystemPromptAsync(assistant, "Override prompt");

        Assert.Multiple(() =>
        {
            Assert.That(result.PromptId, Is.Null);
            Assert.That(result.Template, Is.EqualTo("Override prompt"));
            Assert.That(result.UsedFallback, Is.False);
            Assert.That(result.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public async Task ReferenceService_ReturnsMatchingReferencesAndUnresolvedReferences()
    {
        using var database = PromptTestDatabase.Create();
        await database.MigrateAsync();
        var promptService = CreatePromptService(database);
        var prompt = await promptService.CreatePromptAsync(new PromptCreateRequest("Persisted prompt"));
        var missingPromptId = Guid.CreateVersion7();
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(serviceProvider);
        settings.Model.CustomAssistants.Add(new CustomAssistant
        {
            Id = Guid.CreateVersion7(),
            Name = "Uses persisted",
            SystemPromptId = prompt.Id
        });
        settings.Model.CustomAssistants.Add(new CustomAssistant
        {
            Id = Guid.CreateVersion7(),
            Name = "Uses missing",
            SystemPromptId = missingPromptId
        });

        var referenceService = new AssistantPromptReferenceService(settings, promptService);

        var references = referenceService.ListReferences(prompt.Id);
        var unresolved = await referenceService.ListUnresolvedReferencesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(references, Has.Count.EqualTo(1));
            Assert.That(references[0].AssistantName, Is.EqualTo("Uses persisted"));
            Assert.That(unresolved, Has.Count.EqualTo(1));
            Assert.That(unresolved[0].AssistantName, Is.EqualTo("Uses missing"));
            Assert.That(unresolved[0].PromptId, Is.EqualTo(missingPromptId));
            Assert.That(unresolved[0].Diagnostic.Code, Is.EqualTo(PromptDiagnosticCode.UnresolvedReference));
        });
    }

    private static AssistantPromptResolver CreateResolver(PromptTestDatabase database) =>
        new(CreatePromptService(database), NullLogger<AssistantPromptResolver>.Instance);

    private static PromptService CreatePromptService(PromptTestDatabase database) =>
        new(database.Factory, new DefaultPromptProvider());
}
