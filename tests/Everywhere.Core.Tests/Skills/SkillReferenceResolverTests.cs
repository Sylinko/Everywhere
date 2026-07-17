using Everywhere.Skills;

namespace Everywhere.Core.Tests.Skills;

public class SkillReferenceResolverTests
{
    [Test]
    public void Resolve_FullIdMatchesExactly()
    {
        var result = SkillReferenceResolver.Resolve(
            "skill://agents.deepwiki",
            CreateSkills(
                ("codex.deepwiki", SkillSourceRoot.Codex),
                ("agents.deepwiki", SkillSourceRoot.Agents)));

        Assert.That(result.Skill?.Id, Is.EqualTo("agents.deepwiki"));
    }

    [Test]
    public void Resolve_ShortIdIsRejected()
    {
        var result = SkillReferenceResolver.Resolve(
            "skill://deepwiki",
            CreateSkills(
                ("agents.deepwiki", SkillSourceRoot.Agents),
                ("codex.deepwiki", SkillSourceRoot.Codex)));

        Assert.That(result.Skill, Is.Null);
    }

    [Test]
    public void Resolve_SourceSlashReferenceIsRejected()
    {
        var skills = CreateSkills(
            ("agents.deepwiki", SkillSourceRoot.Agents),
            ("codex.deepwiki", SkillSourceRoot.Codex));

        var slash = SkillReferenceResolver.Resolve("skill://codex/deepwiki", skills);

        Assert.That(slash.Skill, Is.Null);
    }

    [Test]
    public void Resolve_MissingReferenceReturnsEmptyResult()
    {
        var result = SkillReferenceResolver.Resolve(
            "skill://unknown.missing",
            CreateSkills(("codex.deepwiki", SkillSourceRoot.Codex)));

        Assert.That(result.Skill, Is.Null);
    }

    private static SkillDescriptor CreateSkill(string id, SkillSourceRoot root) => new()
    {
        Id = id,
        Name = id,
        FilePath = Path.Combine(Path.GetTempPath(), id, "SKILL.md"),
        MarkdownContent = "# Skill",
        MarkdownBody = "Skill body.",
        SourceRoot = root,
        SourceName = SkillSource.GetSourceId(root),
        SourceDirectoryPath = Path.GetTempPath(),
        IsValid = true,
        IsEnabled = true
    };

    private static IReadOnlyDictionary<string, SkillDescriptor> CreateSkills(
        params (string Id, SkillSourceRoot Root)[] skills) =>
        skills.ToDictionary(
            skill => skill.Id,
            skill => CreateSkill(skill.Id, skill.Root),
            StringComparer.OrdinalIgnoreCase);
}
