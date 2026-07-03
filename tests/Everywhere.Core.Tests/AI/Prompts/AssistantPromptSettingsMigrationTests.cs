using System.Text.Json.Nodes;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Configuration;
using Everywhere.Configuration.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.AI.Prompts;

public sealed class AssistantPromptSettingsMigrationTests
{
    [Test]
    public async Task SettingsMigration_ImportsLegacySystemPromptIntoPromptDatabase()
    {
        using var database = PromptTestDatabase.Create();
        await database.MigrateAsync();
        using var file = TestSettingsFile(CreateSettingsJson(
            AssistantJson(Guid.CreateVersion7(), "Coder", "Custom\r\ninstructions")));

        RunPromptSettingsMigration(file.Path, database);

        var root = ReadRoot(file.Path);
        var assistant = ReadAssistant(root, 0);
        var promptId = ReadPromptId(assistant);
        await using var dbContext = await database.Factory.CreateDbContextAsync();
        var prompt = await dbContext.Prompts.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(root["Version"]!.GetValue<string>(), Is.EqualTo("0.8.1-canary.20260702.12"));
            Assert.That(promptId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(assistant.ContainsKey("SystemPrompt"), Is.False);
            Assert.That(prompt.Id, Is.EqualTo(promptId));
            Assert.That(prompt.Name, Is.EqualTo("Coder system prompt"));
            Assert.That(prompt.Template, Is.EqualTo("Custom\r\ninstructions"));
            Assert.That(prompt.Source, Is.EqualTo(PromptSource.Migration));
        });
    }

    [Test]
    public async Task SettingsMigration_DeduplicatesPromptsByNormalizedLineEndings()
    {
        using var database = PromptTestDatabase.Create();
        await database.MigrateAsync();
        using var file = TestSettingsFile(CreateSettingsJson(
            AssistantJson(Guid.CreateVersion7(), "One", "Shared\r\nbody"),
            AssistantJson(Guid.CreateVersion7(), "Two", "Shared\nbody")));

        RunPromptSettingsMigration(file.Path, database);

        var root = ReadRoot(file.Path);
        var firstPromptId = ReadPromptId(ReadAssistant(root, 0));
        var secondPromptId = ReadPromptId(ReadAssistant(root, 1));
        await using var dbContext = await database.Factory.CreateDbContextAsync();

        Assert.Multiple(() =>
        {
            Assert.That(firstPromptId, Is.EqualTo(secondPromptId));
            Assert.That(dbContext.Prompts.Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SettingsMigration_MapsEmptyDefaultAndMissingLegacyPromptsToDefaultPromptId()
    {
        using var database = PromptTestDatabase.Create();
        await database.MigrateAsync();
        using var file = TestSettingsFile(CreateSettingsJson(
            AssistantJson(Guid.CreateVersion7(), "Empty", string.Empty),
            AssistantJson(Guid.CreateVersion7(), "Default", DefaultPrompts.DefaultSystemPrompt),
            AssistantJson(Guid.CreateVersion7(), "Missing")));

        RunPromptSettingsMigration(file.Path, database);

        var root = ReadRoot(file.Path);
        await using var dbContext = await database.Factory.CreateDbContextAsync();

        Assert.Multiple(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                var assistant = ReadAssistant(root, i);
                Assert.That(ReadPromptId(assistant), Is.EqualTo(Guid.Empty));
                Assert.That(assistant.ContainsKey("SystemPrompt"), Is.False);
            }

            Assert.That(dbContext.Prompts.Count(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task SettingsMigration_RerunReusesDatabasePromptWhenJsonStillHasLegacyField()
    {
        using var database = PromptTestDatabase.Create();
        await database.MigrateAsync();
        using var firstFile = TestSettingsFile(CreateSettingsJson(
            AssistantJson(Guid.CreateVersion7(), "First", "Reusable prompt")));
        using var secondFile = TestSettingsFile(CreateSettingsJson(
            AssistantJson(Guid.CreateVersion7(), "Second", "Reusable prompt")));

        RunPromptSettingsMigration(firstFile.Path, database);
        var firstPromptId = ReadPromptId(ReadAssistant(ReadRoot(firstFile.Path), 0));

        RunPromptSettingsMigration(secondFile.Path, database);
        var secondPromptId = ReadPromptId(ReadAssistant(ReadRoot(secondFile.Path), 0));

        await using var dbContext = await database.Factory.CreateDbContextAsync();
        Assert.Multiple(() =>
        {
            Assert.That(secondPromptId, Is.EqualTo(firstPromptId));
            Assert.That(dbContext.Prompts.Count(), Is.EqualTo(1));
        });
    }

    private static void RunPromptSettingsMigration(string path, PromptTestDatabase database)
    {
        var migrator = new SettingsMigrator(
            path,
            [CreatePromptMigration(database)],
            NullLogger<SettingsMigrator>.Instance);

        migrator.Migrate();
    }

    private static _20260702120000_0_8_1_canary_20260702_12 CreatePromptMigration(PromptTestDatabase database) =>
        new(database.Factory, NullLogger<_20260702120000_0_8_1_canary_20260702_12>.Instance);

    private static string CreateSettingsJson(params JsonObject[] assistants) =>
        CreateSettingsRoot(assistants).ToJsonString();

    private static JsonObject CreateSettingsRoot(params JsonObject[] assistants)
    {
        var assistantArray = new JsonArray();
        foreach (var assistant in assistants)
        {
            assistantArray.Add(assistant.DeepClone());
        }

        return new JsonObject
        {
            ["Version"] = "0.8.1-canary.20260629.12",
            ["Model"] = new JsonObject
            {
                ["CustomAssistants"] = assistantArray
            }
        };
    }

    private static JsonObject AssistantJson(Guid id, string name, string? systemPrompt = null)
    {
        var assistant = new JsonObject
        {
            ["Id"] = id.ToString("D"),
            ["Name"] = name
        };

        if (systemPrompt is not null)
        {
            assistant["SystemPrompt"] = systemPrompt;
        }

        return assistant;
    }

    private static JsonObject ReadRoot(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static JsonObject ReadAssistant(JsonObject root, int index) =>
        root["Model"]!.AsObject()["CustomAssistants"]!.AsArray()[index]!.AsObject();

    private static Guid ReadPromptId(JsonObject assistant) =>
        Guid.Parse(assistant["SystemPromptId"]!.GetValue<string>());

    private static TempSettingsFile TestSettingsFile(string json) => new(json);

    private sealed class TempSettingsFile : IDisposable
    {
        private readonly string _directory;

        public TempSettingsFile(string json)
        {
            _directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Everywhere.Core.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "settings.json");
            File.WriteAllText(Path, json);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }
}
