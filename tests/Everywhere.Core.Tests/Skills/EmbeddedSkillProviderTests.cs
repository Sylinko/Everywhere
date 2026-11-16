using System.Text.RegularExpressions;
using Everywhere.AI;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Configuration;
using Everywhere.Skills;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Everywhere.Core.Tests.Skills;

public partial class EmbeddedSkillProviderTests
{
    [Test]
    public async Task ListAsync_IncludesBuiltInEverywhereAndOfficeCli()
    {
        var provider = new EmbeddedSkillProvider();
        var skills = new List<VirtualSkill>();
        await foreach (var skill in provider.ListAsync())
        {
            skills.Add(skill);
        }

        var everywhereSkill = skills.FirstOrDefault(skill => skill.Id == "builtin.everywhere");
        Assert.Multiple(() =>
        {
            Assert.That(skills.Select(skill => skill.Id), Does.Contain("builtin.officecli"));
            Assert.That(everywhereSkill, Is.Not.Null);
            Assert.That(everywhereSkill!.Resources.ContainsKey("references/ui-navigation-and-operations.md"), Is.True);
        });

        var parsed = SkillParser.Parse("skill://builtin.everywhere/SKILL.md", "everywhere", everywhereSkill!.MarkdownContent);
        Assert.That(parsed.Diagnostics, Is.Empty);
    }

    /// <summary>
    /// Protects the built-in default-enabled skill contract: embedded Everywhere skill metadata,
    /// prompt injection, and all documented reference files must load cleanly on startup.
    /// </summary>
    [Test]
    public async Task SkillManager_LoadsEmbeddedEverywhereSkillAsDefaultEnabledAndReadable()
    {
        using var source = new SkillSource(Substitute.For<ILogger<SkillSource>>(), () => []);
        var manager = new SkillManager(
            new PersistentState(new InMemoryKeyValueStorage()),
            source,
            [new EmbeddedSkillProvider()],
            Substitute.For<ILogger<SkillManager>>());

        await manager.RefreshAsync();

        var prompt = manager.GetPrompt(ToolCallStatus.Enabled);
        var factory = new FileHandlerContextFactory([new SkillFileHandler(manager)]);
        var workingDirectory = Path.GetTempPath();
        var skillContext = await factory.CreateAsync("skill://builtin.everywhere/SKILL.md", workingDirectory);
        var skillReadResult = await skillContext.Handler.ReadAsync(skillContext, 1, 2000, CancellationToken.None);
        var skillMarkdown = string.Join('\n', skillReadResult.Items.Select(static item => item.Content));

        var referenceMatches = ReferenceLinkRegex().Matches(skillMarkdown);
        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("<name>everywhere</name>"));
            Assert.That(prompt, Does.Contain("<file>skill://builtin.everywhere/SKILL.md</file>"));
            Assert.That(skillReadResult.HasMore, Is.False);
            Assert.That(referenceMatches, Is.Not.Empty);
        });

        foreach (Match match in referenceMatches)
        {
            var relativePath = match.Groups["path"].Value;
            var referenceContext = await factory.CreateAsync($"skill://builtin.everywhere/{relativePath}", workingDirectory);
            var referenceRead = await referenceContext.Handler.ReadAsync(referenceContext, 1, 2000, CancellationToken.None);
            Assert.That(referenceRead.Items, Is.Not.Empty, $"Expected embedded skill reference '{relativePath}' to be non-empty.");
        }
    }

    [GeneratedRegex(@"\((?<path>references/[a-z0-9\-]+\.md)\)")]
    private static partial Regex ReferenceLinkRegex();
}