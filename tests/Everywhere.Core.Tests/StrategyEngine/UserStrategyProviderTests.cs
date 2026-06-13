using Everywhere.I18N;
using Everywhere.StrategyEngine;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.StrategyEngine;

public class UserStrategyProviderTests
{
    [Test]
    public void GetStrategies_LoadsStrategyFilesFromUserRoot()
    {
        using var workspace = TestWorkspace.Create();
        var strategyPath = workspace.Write(
            "summarize/STRATEGY.md",
            """
            ---
            id: user.ignored
            name: summarize
            title: Summarize
            priority: 42
            when: true
            ---

            Summarize {Argument}
            """);
        var provider = CreateProvider(workspace.Root);

        var strategies = provider.GetStrategies().ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(strategies, Has.Length.EqualTo(1));
            Assert.That(strategies[0].Id, Is.EqualTo("user.summarize"));
            Assert.That(strategies[0].NameKey.ToString(), Is.EqualTo("Summarize"));
            Assert.That(strategies[0].Priority, Is.EqualTo(42));
            Assert.That(strategies[0].Body, Is.EqualTo("\nSummarize {Argument}"));
            Assert.That(strategies[0].Source.ProviderId, Is.EqualTo("user"));
            Assert.That(strategies[0].Source.Location.LocalPath, Is.EqualTo(strategyPath));
            Assert.That(provider.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public void GetStrategies_LoadsNestedStrategyFilesDeterministically()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write(
            "b/STRATEGY.md",
            """
            ---
            name: b
            title: B
            ---
            """);
        workspace.Write(
            "a/deeper/STRATEGY.md",
            """
            ---
            id: whatever
            name: deeper
            title: A
            ---
            """);
        var provider = CreateProvider(workspace.Root);

        var strategies = provider.GetStrategies().ToArray();

        Assert.That(strategies.Select(strategy => strategy.Id), Is.EqualTo(new[] { "user.deeper", "user.b" }));
    }

    [Test]
    public void GetStrategies_InvalidStrategyProducesDiagnosticsButNoStrategy()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write(
            "broken/STRATEGY.md",
            """
            ---
            title: Broken
            ---
            """);
        var provider = CreateProvider(workspace.Root);

        var strategies = provider.GetStrategies().ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(strategies, Is.Empty);
            Assert.That(provider.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.missing_name"));
        });
    }

    [Test]
    public void Registry_PreservesUserStrategyFileSource()
    {
        using var workspace = TestWorkspace.Create();
        var strategyPath = workspace.Write(
            "summarize/STRATEGY.md",
            """
            ---
            id: user.anything
            name: summarize
            title: Summarize
            ---
            """);
        var provider = CreateProvider(workspace.Root);
        var registry = new StrategyRegistry([provider]);

        var strategy = registry.GetRegisteredStrategies().Single();

        Assert.Multiple(() =>
        {
            Assert.That(strategy.Id, Is.EqualTo("user.summarize"));
            Assert.That(strategy.Source.Location.LocalPath, Is.EqualTo(strategyPath));
        });
    }

    [Test]
    public void StrategyEngine_UserStrategyCanMatchAndBeSelected()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write(
            "global/STRATEGY.md",
            """
            ---
            name: global
            title: Global
            when: true
            ---

            Hello.
            """);
        var provider = CreateProvider(workspace.Root);
        var engine = new global::Everywhere.StrategyEngine.StrategyEngine(
            new StrategyRegistry([provider]),
            NullLogger<global::Everywhere.StrategyEngine.StrategyEngine>.Instance);

        var strategies = engine.GetStrategies(StrategyContext.FromAttachments([]));

        Assert.That(strategies.Select(strategy => strategy.Id), Does.Contain("user.global"));
    }

    [Test]
    public void GetStrategies_NameWithProviderPrefixWarnsAndUsesLocalNameId()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write(
            "foo/STRATEGY.md",
            """
            ---
            name: user.foo
            ---
            """);
        var provider = CreateProvider(workspace.Root);

        var strategy = provider.GetStrategies().Single();

        Assert.Multiple(() =>
        {
            Assert.That(strategy.Id, Is.EqualTo("user.foo"));
            Assert.That(provider.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.name_has_provider_prefix"));
            Assert.That(provider.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.name_folder_mismatch"));
        });
    }

    [Test]
    public void GetStrategies_NameFolderMismatchWarnsAndStrategyStillLoads()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write(
            "actual-folder/STRATEGY.md",
            """
            ---
            name: different-name
            title: Display
            ---
            """);
        var provider = CreateProvider(workspace.Root);

        var strategy = provider.GetStrategies().Single();

        Assert.Multiple(() =>
        {
            Assert.That(strategy.Id, Is.EqualTo("user.different-name"));
            Assert.That(strategy.NameKey.ToString(), Is.EqualTo("Display"));
            Assert.That(provider.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.name_folder_mismatch"));
            Assert.That(provider.Diagnostics.Where(diagnostic => diagnostic.Severity == StrategyDiagnosticSeverity.Error), Is.Empty);
        });
    }

    [Test]
    public void GetStrategies_TitleMappingControlsDisplayName()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write(
            "localized/STRATEGY.md",
            """
            ---
            name: localized
            title:
              en: Localized
              zh-hans: 本地化
            ---
            """);
        var provider = CreateProvider(workspace.Root);

        var strategy = provider.GetStrategies().Single();

        Assert.Multiple(() =>
        {
            Assert.That(strategy.Id, Is.EqualTo("user.localized"));
            Assert.That(strategy.NameKey, Is.TypeOf<JsonDynamicResourceKey>());
            Assert.That(((JsonDynamicResourceKey)strategy.NameKey)["en"], Is.EqualTo("Localized"));
        });
    }

    [Test]
    public void GetStrategies_MissingTitleFallsBackToName()
    {
        using var workspace = TestWorkspace.Create();
        workspace.Write(
            "plain/STRATEGY.md",
            """
            ---
            name: plain
            ---
            """);
        var provider = CreateProvider(workspace.Root);

        var strategy = provider.GetStrategies().Single();

        Assert.That(strategy.NameKey.ToString(), Is.EqualTo("plain"));
    }

    private static UserStrategyProvider CreateProvider(string root) =>
        new(
            new TestUserStrategySource(root),
            new StrategyDefinitionV1Normalizer(),
            [new RelativeFileStrategySourceResolver(), new AbsoluteFileStrategySourceResolver()],
            NullLogger<UserStrategyProvider>.Instance);

    private sealed class TestUserStrategySource(string root) : IUserStrategySource
    {
        public string RootDirectoryPath { get; } = root;

        public IEnumerable<string> EnumerateStrategyFiles() =>
            Directory.EnumerateFiles(RootDirectoryPath, "STRATEGY.md", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"EverywhereUserStrategyTests-{Guid.NewGuid():N}");

        public string Root => _root;

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
