using Everywhere.Common.Frontmatter;

namespace Everywhere.Core.Tests.Common;

public class MarkdownFrontmatterParserTests
{
    [Test]
    public void Parse_SplitsFrontmatterAndBody()
    {
        var document = MarkdownFrontmatterParser.Parse(
            """
            ---
            name: demo
            ---

            Body.
            """);

        Assert.Multiple(() =>
        {
            Assert.That(document.HasFrontmatter, Is.True);
            Assert.That(document.RawFrontmatter, Is.EqualTo("name: demo"));
            Assert.That(document.Body, Is.EqualTo("\nBody."));
        });
    }

    [Test]
    public void Parse_NormalizesLineEndings()
    {
        var document = MarkdownFrontmatterParser.Parse("---\r\nname: demo\r\n---\r\n\r\nBody.\r\n");

        Assert.Multiple(() =>
        {
            Assert.That(document.Content, Is.EqualTo("---\nname: demo\n---\n\nBody.\n"));
            Assert.That(document.Body, Is.EqualTo("\nBody.\n"));
        });
    }

    [Test]
    public void Parse_NoFrontmatterTreatsAllContentAsBody()
    {
        var document = MarkdownFrontmatterParser.Parse("# Heading\n\nBody.");

        Assert.Multiple(() =>
        {
            Assert.That(document.HasFrontmatter, Is.False);
            Assert.That(document.RawFrontmatter, Is.Null);
            Assert.That(document.Body, Is.EqualTo("# Heading\n\nBody."));
        });
    }

    [Test]
    public void YamlFrontmatterParser_InvalidYamlReturnsDiagnostic()
    {
        var result = YamlFrontmatterParser.ParseMapping("name: [");

        Assert.Multiple(() =>
        {
            Assert.That(result.Values, Is.Null);
            Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id), Is.EqualTo(new[] { "frontmatter.invalid_yaml" }));
        });
    }

    [Test]
    public void YamlValueReader_ReadsScalarsListsMapsAndDurations()
    {
        var parseResult = YamlFrontmatterParser.ParseMapping(
            """
            name: demo
            flag: true
            priority: 10
            preprocessors:
              - selected-text
            options:
              timeout: 300ms
            """);
        var values = parseResult.Values!;
        var diagnostics = new List<FrontmatterDiagnostic>();
        var options = YamlValueReader.ReadMap(values, "options", diagnostics)!;

        Assert.Multiple(() =>
        {
            Assert.That(YamlValueReader.ReadString(values, "name", diagnostics), Is.EqualTo("demo"));
            Assert.That(YamlValueReader.ReadBool(values, "flag", diagnostics), Is.True);
            Assert.That(YamlValueReader.ReadInt(values, "priority", diagnostics), Is.EqualTo(10));
            Assert.That(YamlValueReader.ReadStringList(values, "preprocessors", diagnostics), Is.EqualTo(new[] { "selected-text" }));
            Assert.That(YamlValueReader.ReadDurationString(options, "timeout", diagnostics), Is.EqualTo("300ms"));
            Assert.That(YamlValueReader.TryParseDuration("2s", out var duration), Is.True);
            Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(diagnostics, Is.Empty);
        });
    }

    [Test]
    public void YamlValueReader_InvalidDurationReturnsDiagnostic()
    {
        var parseResult = YamlFrontmatterParser.ParseMapping("timeout: 2m");
        var diagnostics = new List<FrontmatterDiagnostic>();

        var value = YamlValueReader.ReadDurationString(parseResult.Values!, "timeout", diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo("2m"));
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Id), Is.EqualTo(new[] { "frontmatter.invalid_duration" }));
        });
    }
}
