using Everywhere.Collections;
using Everywhere.Skills;
using Everywhere.StrategyEngine;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Core.Tests.StrategyEngine;

public class StrategyDefinitionV1NormalizerTests
{
    [Test]
    public async Task Normalize_DerivesBodyFromRelativeSkill()
    {
        using var workspace = TestWorkspace.Create();
        var skillPath = workspace.Write(
            "Polite/SKILL.md",
            """
            ---
            name: Polite
            description: Rewrite politely.
            ---

            Follow these writing rules.
            """);
        var strategyPath = workspace.Write(
            "Polite/polite.strategy.md",
            """
            ---
            id: user.polite
            from: ./SKILL.md
            ---
            """);

        var result = await NormalizeFileAsync(strategyPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Diagnostics.Where(d => d.Severity == StrategyDiagnosticSeverity.Error), Is.Empty);
            Assert.That(result.Strategy, Is.Not.Null);
            Assert.That(result.Strategy!.Body, Is.EqualTo("\nFollow these writing rules."));
            Assert.That(result.Strategy.NameKey.ToString(), Is.EqualTo("Polite"));
            Assert.That(result.Strategy.Includes.Single().Location.LocalPath, Is.EqualTo(skillPath));
        });
    }

    [Test]
    public async Task Normalize_CurrentBodyReplacesSourceBody()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write("Base/SKILL.md", "# Base\n\nBase body.");
        var strategyPath = workspace.Write(
            "Base/rewrite.strategy.md",
            """
            ---
            id: user.rewrite
            from: ./SKILL.md
            name: Rewrite
            ---

            Current body.
            """);

        var result = await NormalizeFileAsync(strategyPath);

        Assert.That(result.Strategy!.Body, Is.EqualTo("\nCurrent body."));
    }

    [Test]
    public async Task Normalize_OverridesDisplayPriorityConditionAndTools()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write(
            "Base/base.strategy.md",
            """
            ---
            id: user.base
            name: Base
            icon: Info
            priority: 1
            when: false
            tools:
              builtin.web.*: false
            ---

            Base body.
            """);
        var strategyPath = workspace.Write(
            "Base/current.strategy.md",
            """
            ---
            id: user.current
            from: ./base.strategy.md
            name: Current
            icon: FileText
            priority: 90
            when: true
            tools:
              builtin.filesystem.read_file: true
            ---

            Current body.
            """);

        var result = await NormalizeFileAsync(strategyPath);
        var strategy = result.Strategy!;

        Assert.Multiple(() =>
        {
            Assert.That(strategy.Id, Is.EqualTo("user.current"));
            Assert.That(strategy.NameKey.ToString(), Is.EqualTo("Current"));
            Assert.That(strategy.Icon?.Kind, Is.EqualTo(LucideIconKind.FileText));
            Assert.That(strategy.Priority, Is.EqualTo(90));
            Assert.That(strategy.Condition?.Evaluate(StrategyContext.FromAttachments([])), Is.True);
            Assert.That(strategy.ToolRulesets, Is.Not.Null);
            Assert.That(strategy.ToolRulesets!["builtin.filesystem.read_file"], Is.True);
            Assert.That(strategy.ToolRulesets.ContainsKey("builtin.web.*"), Is.False);
        });
    }

    [Test]
    public async Task Normalize_MissingCurrentBodyInheritsSourceBody()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write("Base/SKILL.md", "# Base\n\nBase body.");
        var strategyPath = workspace.Write(
            "Base/inherit.strategy.md",
            """
            ---
            id: user.inherit
            from: ./SKILL.md
            name: Inherit
            ---
            """);

        var result = await NormalizeFileAsync(strategyPath);

        Assert.That(result.Strategy!.Body, Is.EqualTo("# Base\n\nBase body."));
    }

    [Test]
    public async Task Normalize_NestedFromProducesDiagnostic()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write("Nested/SKILL.md", "# Base\n\nBase body.");
        workspace.Write(
            "Nested/base.strategy.md",
            """
            ---
            id: user.base
            from: ./SKILL.md
            name: Base
            ---
            """);
        var strategyPath = workspace.Write(
            "Nested/current.strategy.md",
            """
            ---
            id: user.current
            from: ./base.strategy.md
            name: Current
            ---
            """);

        var result = await NormalizeFileAsync(strategyPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Strategy, Is.Null);
            Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.nested_from"));
        });
    }

    [Test]
    public async Task Normalize_DerivesBodyFromManagedSkillReference()
    {
        using var workspace = TestWorkspace.Create();
        var strategyPath = workspace.Write(
            "managed.strategy.md",
            """
            ---
            id: user.managed
            from: skill://codex/deepwiki
            name: Managed
            ---
            """);
        var skill = CreateSkill("codex.deepwiki", SkillSourceRoot.Codex, "Managed skill body.");

        var result = await NormalizeFileAsync(strategyPath, [skill]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Diagnostics.Where(d => d.Severity == StrategyDiagnosticSeverity.Error), Is.Empty);
            Assert.That(result.Strategy!.Body, Is.EqualTo("Managed skill body."));
            Assert.That(result.Strategy.Includes.Single().Location.LocalPath, Is.EqualTo(skill.FilePath));
        });
    }

    [Test]
    public async Task Normalize_ShortManagedSkillReferenceWarnsWhenAmbiguous()
    {
        using var workspace = TestWorkspace.Create();
        var strategyPath = workspace.Write(
            "managed.strategy.md",
            """
            ---
            id: user.managed
            from: skill://deepwiki
            name: Managed
            ---
            """);

        var result = await NormalizeFileAsync(
            strategyPath,
            [
                CreateSkill("codex.deepwiki", SkillSourceRoot.Codex, "Codex body."),
                CreateSkill("agents.deepwiki", SkillSourceRoot.Agents, "Agents body.")
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Strategy!.Body, Is.EqualTo("Agents body."));
            Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.ambiguous_skill_reference"));
        });
    }

    [Test]
    public async Task Normalize_RejectsBuiltinNamespaceForUserProvider()
    {
        using var workspace = TestWorkspace.Create();
        var strategyPath = workspace.Write(
            "bad.strategy.md",
            """
            ---
            id: builtin.bad
            name: Bad
            ---

            Body.
            """);

        var result = await NormalizeFileAsync(strategyPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Strategy, Is.Null);
            Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.invalid_id"));
        });
    }

    private static async Task<StrategyNormalizationResult> NormalizeFileAsync(
        string strategyPath,
        IReadOnlyList<SkillDescriptor>? skills = null)
    {
        var document = StrategyDocumentParser.Parse(strategyPath, await File.ReadAllTextAsync(strategyPath), "user");
        var normalizer = new StrategyDefinitionV1Normalizer();
        return await normalizer.NormalizeAsync(document, CreateLoadContext(skills), CancellationToken.None);
    }

    private static StrategyLoadContext CreateLoadContext(IReadOnlyList<SkillDescriptor>? skills = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<ISkillManager>(new TestSkillManager(skills ?? []))
            .BuildServiceProvider();

        return new StrategyLoadContext
        {
            SourceResolvers =
            [
                new RelativeFileStrategySourceResolver(),
                new AbsoluteFileStrategySourceResolver(),
                new SkillStrategySourceResolver(services)
            ]
        };
    }

    private static SkillDescriptor CreateSkill(string id, SkillSourceRoot root, string body) => new()
    {
        Id = id,
        Name = id,
        DirectoryName = id.Split('.').Last(),
        FilePath = Path.Combine(Path.GetTempPath(), id, "SKILL.md"),
        MarkdownContent = $"# {id}\n\n{body}",
        MarkdownBody = body,
        SourceRoot = root,
        SourceName = SkillSource.GetSourceId(root),
        SourceDirectoryPath = Path.GetTempPath(),
        IsValid = true,
        IsEnabled = false
    };

    private sealed class TestSkillManager(IReadOnlyList<SkillDescriptor> skills) : ISkillManager
    {
        public IReadOnlyBindableList<SkillSourceGroup> SourceGroups { get; } = new BindableList<SkillSourceGroup>();

        public SkillResolutionResult ResolveSkillReference(string reference) =>
            SkillReferenceResolver.Resolve(reference, skills);

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"EverywhereStrategyTests-{Guid.NewGuid():N}");

        private TestWorkspace() => Directory.CreateDirectory(_root);

        public static TestWorkspace Create() => new();

        public string Write(string relativePath, string content)
        {
            var filePath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
            return filePath;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
