using Everywhere.Skills;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Everywhere.Core.Tests;

public class SkillSourceTests
{
    [Test]
    public async Task RootWatcher_DebouncesAndAggregatesSkillMarkdownChanges()
    {
        using var temp = new TempDirectory();
        var rootPath = Path.Combine(temp.Path, "skills");
        Directory.CreateDirectory(rootPath);

        using var source = CreateSource(rootPath);
        var collector = new ChangeCollector(source);

        var firstSkillFilePath = Path.Combine(rootPath, "alpha", "SKILL.md");
        var secondSkillFilePath = Path.Combine(rootPath, "beta", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(firstSkillFilePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondSkillFilePath)!);
        await File.WriteAllTextAsync(firstSkillFilePath, "# Alpha");
        await File.WriteAllTextAsync(secondSkillFilePath, "# Beta");

        var changes = await collector.WaitForChangesAsync();
        await Task.Delay(1200);

        Assert.Multiple(() =>
        {
            Assert.That(collector.EventCount, Is.EqualTo(1));
            Assert.That(
                changes.Select(change => change.SkillFilePath),
                Does.Contain(firstSkillFilePath).And.Contain(secondSkillFilePath));
            Assert.That(
                changes.Select(change => change.SkillDirectoryPath),
                Does.Contain(Path.GetDirectoryName(firstSkillFilePath)).And.Contain(Path.GetDirectoryName(secondSkillFilePath)));
        });
    }

    [Test]
    public async Task RootWatcher_ReportsDirectSkillDirectoryChanges()
    {
        using var temp = new TempDirectory();
        var rootPath = Path.Combine(temp.Path, "skills");
        Directory.CreateDirectory(rootPath);

        using var source = CreateSource(rootPath);
        var collector = new ChangeCollector(source);

        var skillDirectoryPath = Path.Combine(rootPath, "alpha");
        Directory.CreateDirectory(skillDirectoryPath);

        var changes = await collector.WaitForChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(changes.Select(change => change.SkillDirectoryPath), Does.Contain(skillDirectoryPath));
            Assert.That(changes.Where(change => change.SkillDirectoryPath == skillDirectoryPath).All(change => change.SkillFilePath is null), Is.True);
        });
    }

    [Test]
    public async Task RootWatcher_IgnoresNonSkillResourceChanges()
    {
        using var temp = new TempDirectory();
        var rootPath = Path.Combine(temp.Path, "skills");
        var skillDirectoryPath = Path.Combine(rootPath, "alpha");
        Directory.CreateDirectory(skillDirectoryPath);

        using var source = CreateSource(rootPath);
        var collector = new ChangeCollector(source);

        var assetPath = Path.Combine(skillDirectoryPath, "references", "notes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllTextAsync(assetPath, "ignored");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "README.md"), "ignored");

        Assert.That(await collector.WaitForQuietAsync(), Is.True);
    }

    [Test]
    public async Task Dispose_StopsRootWatcher()
    {
        using var temp = new TempDirectory();
        var rootPath = Path.Combine(temp.Path, "skills");
        Directory.CreateDirectory(rootPath);

        using var source = CreateSource(rootPath);
        var collector = new ChangeCollector(source);
        source.Dispose();

        var skillFilePath = Path.Combine(rootPath, "alpha", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillFilePath)!);
        await File.WriteAllTextAsync(skillFilePath, "# Alpha");

        Assert.That(await collector.WaitForQuietAsync(), Is.True);
    }

    private static SkillSource CreateSource(string rootPath) =>
        new(Substitute.For<ILogger<SkillSource>>(), () => [CreateRoot(rootPath)]);

    private static SkillRoot CreateRoot(string path) =>
        new(SkillSourceRoot.Everywhere, "Test", path);

    private sealed class ChangeCollector
    {
        private readonly SkillSource _source;
        private readonly Lock _lock = new();
        private readonly TaskCompletionSource<IReadOnlyList<SkillSourceChange>> _changesSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChangeCollector(SkillSource source)
        {
            _source = source;
            _source.Changed += HandleChanged;
        }

        public int EventCount { get; private set; }

        public async Task<IReadOnlyList<SkillSourceChange>> WaitForChangesAsync()
        {
            var changesTask = _changesSource.Task;
            var completed = await Task.WhenAny(changesTask, Task.Delay(TimeSpan.FromSeconds(6)));
            Assert.That(completed, Is.EqualTo(changesTask), "Timed out waiting for skill source changes.");
            return await _changesSource.Task;
        }

        public async Task<bool> WaitForQuietAsync()
        {
            var changesTask = _changesSource.Task;
            var completed = await Task.WhenAny(changesTask, Task.Delay(TimeSpan.FromMilliseconds(1300)));
            return completed != changesTask;
        }

        private void HandleChanged(object? sender, SkillSourceChangedEventArgs args)
        {
            lock (_lock)
            {
                EventCount++;
                _changesSource.TrySetResult(args.Changes);
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Everywhere.SkillSourceTests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup best effort.
            }
        }
    }
}
