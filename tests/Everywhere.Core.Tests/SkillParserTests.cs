using Everywhere.Skills;

namespace Everywhere.Core.Tests;

public class SkillParserTests
{
    [Test]
    public void Parse_ReadsOfficialFrontmatterFieldsOnly()
    {
        var result = SkillParser.Parse(
            @"C:\skills\review\SKILL.md",
            "code-review",
            """
            ---
            name: code-review
            description: Review code when the user asks for feedback.
            from: skill://not-a-skill-field
            tools:
              web: true
            ---

            # Ignored heading

            Body paragraph.
            """);

        Assert.Multiple(() =>
        {
            Assert.That(result.FrontmatterName, Is.EqualTo("code-review"));
            Assert.That(result.FrontmatterDescription, Is.EqualTo("Review code when the user asks for feedback."));
            Assert.That(result.HeadingName, Is.EqualTo("Ignored heading"));
            Assert.That(result.FirstParagraph, Is.EqualTo("Body paragraph."));
            Assert.That(result.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public void Parse_UsesMarkdownFallbacksWhenFrontmatterIsMissing()
    {
        var result = SkillParser.Parse(
            @"C:\skills\writer\SKILL.md",
            "writer",
            """
            # Writing Helper

            Help rewrite text in the requested tone.
            """);

        Assert.Multiple(() =>
        {
            Assert.That(result.FrontmatterName, Is.Null);
            Assert.That(result.FrontmatterDescription, Is.Null);
            Assert.That(result.HeadingName, Is.EqualTo("Writing Helper"));
            Assert.That(result.FirstParagraph, Is.EqualTo("Help rewrite text in the requested tone."));
            Assert.That(result.DirectoryName, Is.EqualTo("writer"));
            Assert.That(result.Diagnostics.Select(d => d.Id), Is.EquivalentTo(new[] { "skill.missing_name", "skill.missing_description" }));
        });
    }

    [Test]
    public void Parse_UnquotesSimpleYamlScalars()
    {
        var result = SkillParser.Parse(
            @"C:\skills\hello\SKILL.md",
            "hello",
            """
            ---
            name: "hello"
            description: 'Greet the user.'
            ---

            Greet warmly.
            """);

        Assert.Multiple(() =>
        {
            Assert.That(result.FrontmatterName, Is.EqualTo("hello"));
            Assert.That(result.FrontmatterDescription, Is.EqualTo("Greet the user."));
            Assert.That(result.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public void Parse_NameFolderMismatch_IsWarningOnly()
    {
        var result = SkillParser.Parse(
            @"C:\skills\folder-name\SKILL.md",
            "folder-name",
            """
            ---
            name: Display Name
            description: Valid description.
            ---

            Body.
            """);

        Assert.That(result.Diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "skill.name_folder_mismatch" }));
    }

    [Test]
    public void Parse_ReadsSimpleMetadataAsDictionary()
    {
        var result = SkillParser.Parse(
            @"C:\skills\metadata\SKILL.md",
            "metadata",
            """
            ---
            name: metadata
            description: Valid description.
            license: MIT
            compatibility: Desktop
            author: Everywhere
            version: 1.0.0
            ---

            Body.
            """);

        Assert.Multiple(() =>
        {
            Assert.That(result.Metadata["license"], Is.EqualTo("MIT"));
            Assert.That(result.Metadata["compatibility"], Is.EqualTo("Desktop"));
            Assert.That(result.Metadata["author"], Is.EqualTo("Everywhere"));
            Assert.That(result.Metadata["version"], Is.EqualTo("1.0.0"));
            Assert.That(result.MarkdownBody.Trim(), Is.EqualTo("Body."));
        });
    }

    [Test]
    public void Parse_FlattensNestedMetadataSection()
    {
        var result = SkillParser.Parse(
            @"C:\skills\metadata\SKILL.md",
            "metadata",
            """
            ---
            name: metadata
            description: Valid description.
            metadata:
              author: Codex Team
              version: "2.0.0"
            ---

            Body.
            """);

        Assert.Multiple(() =>
        {
            Assert.That(result.Metadata["metadata.author"], Is.EqualTo("Codex Team"));
            Assert.That(result.Metadata["metadata.version"], Is.EqualTo("2.0.0"));
        });
    }
}
