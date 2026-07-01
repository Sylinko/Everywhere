using Everywhere.Skills;

namespace Everywhere.Core.Tests.Skills;

public class SkillReferenceResolverTests
{
    [Test]
    public void Resolve_ShortReferenceUsesSkillSourceRootOrder()
    {
        var result = SkillReferenceResolver.Resolve(
            "skill://deepwiki",
            [
                CreateSkill("codex.deepwiki", SkillSourceRoot.Codex),
                CreateSkill("agents.deepwiki", SkillSourceRoot.Agents)
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Skill?.Id, Is.EqualTo("agents.deepwiki"));
            Assert.That(result.IsAmbiguous, Is.True);
            Assert.That(result.Candidates.Select(skill => skill.Id), Is.EqualTo(new[] { "agents.deepwiki", "codex.deepwiki" }));
        });
    }

    [Test]
    public void Resolve_SourceSlashReferenceMatchesQualifiedSkill()
    {
        var result = SkillReferenceResolver.Resolve(
            "skill://codex/deepwiki",
            [
                CreateSkill("agents.deepwiki", SkillSourceRoot.Agents),
                CreateSkill("codex.deepwiki", SkillSourceRoot.Codex)
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Skill?.Id, Is.EqualTo("codex.deepwiki"));
            Assert.That(result.IsAmbiguous, Is.False);
        });
    }

    [Test]
    public void Resolve_DottedReferenceMatchesSameSkillAsSlashReference()
    {
        var skills = new[]
        {
            CreateSkill("agents.deepwiki", SkillSourceRoot.Agents),
            CreateSkill("codex.deepwiki", SkillSourceRoot.Codex)
        };

        var slash = SkillReferenceResolver.Resolve("skill://codex/deepwiki", skills);
        var dotted = SkillReferenceResolver.Resolve("skill://codex.deepwiki", skills);

        Assert.That(dotted.Skill?.Id, Is.EqualTo(slash.Skill?.Id));
    }

    [Test]
    public void Resolve_MissingReferenceReturnsEmptyResult()
    {
        var result = SkillReferenceResolver.Resolve("skill://missing", [CreateSkill("codex.deepwiki", SkillSourceRoot.Codex)]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Skill, Is.Null);
            Assert.That(result.Candidates, Is.Empty);
        });
    }

    private static SkillDescriptor CreateSkill(string id, SkillSourceRoot root) => new()
    {
        Id = id,
        Name = id,
        DirectoryName = id.Split('.').Last(),
        FilePath = Path.Combine(Path.GetTempPath(), id, "SKILL.md"),
        MarkdownContent = "# Skill",
        MarkdownBody = "Skill body.",
        SourceRoot = root,
        SourceName = SkillSource.GetSourceId(root),
        SourceDirectoryPath = Path.GetTempPath(),
        IsValid = true,
        IsEnabled = true
    };
}
