using System.Text.Json.Nodes;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.Configuration;

/// <summary>
/// Verifies plugin-specific version boundaries and commit ordering with real owned migration resources.
/// </summary>
public sealed class PluginSettingsMigrationTests
{
    /// <summary>
    /// Pending asynchronous cleanup must finish successfully before any new document replaces the old one.
    /// </summary>
    [Test]
    public async Task Migration_PendingCleanup_CommitsAfterDisposalAndPreservesBorrowedService()
    {
        using var file = new SettingsFile("""{"Version":"0.0.0","Value":1}""");
        var trace = new MigrationTrace();
        var pending = Initialize(file.Path, trace);
        await trace.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(File.ReadAllText(file.Path), Is.EqualTo(file.Original));
        trace.Continue.TrySetResult();
        await pending;
        Assert.That(JsonNode.Parse(File.ReadAllText(file.Path))?["Version"]?.GetValue<string>(), Is.EqualTo("2.0.0"));
        Assert.That(trace.Events, Is.EqualTo(new[] { "run:first", "run:second", "dispose:second", "dispose:first" }));
        Assert.That(trace.IsDisposed, Is.False);
        Assert.That(Directory.GetFiles(file.Directory, "settings_backup_*.json"), Has.Length.EqualTo(1));
    }

    /// <summary>
    /// All failures before commit keep the original bytes and still attempt resource cleanup.
    /// </summary>
    [TestCase("migration")]
    [TestCase("validation")]
    [TestCase("disposal")]
    public async Task Migration_FailureBeforeCommit_PreservesOriginalAndCanRetry(string failure)
    {
        using var file = new SettingsFile("""{"Version":"0.0.0","Value":1}""");
        var trace = new MigrationTrace { Failure = failure };
        trace.Continue.TrySetResult();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await Initialize(file.Path, trace));
        Assert.That(File.ReadAllText(file.Path), Is.EqualTo(file.Original));
        // MSDI stops its disposal loop at the first throwing resource; it does not aggregate and continue.
        Assert.That(trace.Events.Contains("dispose:first"), Is.EqualTo(failure != "disposal"));
        Assert.That(trace.IsDisposed, Is.False);
        trace.Failure = null;
        await Initialize(file.Path, trace);
        Assert.That(JsonNode.Parse(File.ReadAllText(file.Path))?["Value"]?.GetValue<int>(), Is.EqualTo(2));
    }

    /// <summary>
    /// Neither a new document nor an up-to-date one constructs obsolete migrations.
    /// </summary>
    [TestCase(null)]
    [TestCase("""{"Version":"2.0.0","Value":4}""")]
    public async Task Migration_NoPendingWork_DoesNotConstructMigrations(string? original)
    {
        using var file = new SettingsFile(original);
        await SettingsMigrator.InitializeAsync(file.Path, new SemanticVersion(2), () => new JsonObject { ["Value"] = 5 },
            _ => { }, services => services
                .AddTransient<SecondMigration>(_ => throw new InvalidOperationException("Must not construct."))
                .AddSettingsMigration<SecondMigration>(), NullLoggerFactory.Instance);
        var saved = JsonNode.Parse(File.ReadAllText(file.Path));
        Assert.That(saved?["Value"]?.GetValue<int>(), Is.EqualTo(original is null ? 5 : 4));
        Assert.That(saved?["Version"]?.GetValue<string>(), Is.EqualTo("2.0.0"));
    }

    /// <summary>
    /// Invalid and newer format versions are not interpreted as an empty file or a reset request.
    /// </summary>
    [TestCase("""{"Version":"3.0.0"}""")]
    [TestCase("""{"Version":"bad"}""")]
    [TestCase("""{"Version":null}""")]
    public void Migration_UnsupportedVersion_PreservesFile(string original)
    {
        using var file = new SettingsFile(original);
        Assert.ThrowsAsync<InvalidDataException>(async () => await SettingsMigrator.InitializeAsync(
            file.Path, new SemanticVersion(2), () => new JsonObject(), _ => { }, null, NullLoggerFactory.Instance));
        Assert.That(File.ReadAllText(file.Path), Is.EqualTo(original));
    }

    /// <summary>
    /// Configures ordered JSON transformations and caller-owned auxiliary state.
    /// </summary>
    private static Task Initialize(string path, MigrationTrace trace)
    {
        return SettingsMigrator.InitializeAsync(path, new SemanticVersion(2), () => new JsonObject(), _ =>
        {
            if (trace.Failure == "validation") throw new InvalidOperationException("Validation failed.");
        }, services => services.AddSingleton(trace).AddSettingsMigration<SecondMigration>().AddSettingsMigration<FirstMigration>(),
            NullLoggerFactory.Instance);
    }

    /// <summary>
    /// Represents a borrowed host dependency that the temporary migration provider must not dispose.
    /// </summary>
    private sealed class MigrationTrace : IDisposable
    {
        /// <summary>
        /// Gets or sets the failure boundary exercised by this run.
        /// </summary>
        public string? Failure { get; set; }

        /// <summary>
        /// Gets the ordered migration and cleanup events.
        /// </summary>
        public List<string> Events { get; } = [];

        /// <summary>
        /// Signals that asynchronous resource cleanup has started.
        /// </summary>
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Allows cleanup to finish independently of the test's observation.
        /// </summary>
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets whether the borrowed dependency was incorrectly disposed.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <inheritdoc />
        public void Dispose() => IsDisposed = true;
    }

    /// <summary>
    /// Makes the first change and observes cleanup even when a later stage fails.
    /// </summary>
    private sealed class FirstMigration(MigrationTrace trace) : SettingsMigration, ISettingsMigrationMetadata, IDisposable
    {
        /// <inheritdoc />
        public static SemanticVersion Version { get; } = new(1);

        /// <inheritdoc />
        protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [Apply];

        /// <summary>
        /// Mutates only the working document.
        /// </summary>
        private bool Apply(JsonObject root)
        {
            trace.Events.Add("run:first");
            root["Value"] = 1;
            return true;
        }

        /// <inheritdoc />
        public void Dispose() => trace.Events.Add("dispose:first");
    }

    /// <summary>
    /// Exercises asynchronous cleanup, including a failure after the working document has changed.
    /// </summary>
    private sealed class SecondMigration(MigrationTrace trace) : SettingsMigration, ISettingsMigrationMetadata, IAsyncDisposable
    {
        /// <inheritdoc />
        public static SemanticVersion Version { get; } = new(2);

        /// <inheritdoc />
        protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [Apply];

        /// <summary>
        /// Changes JSON before an optional migration failure.
        /// </summary>
        private bool Apply(JsonObject root)
        {
            trace.Events.Add("run:second");
            root["Value"] = 2;
            if (trace.Failure == "migration") throw new InvalidOperationException("Migration failed.");
            return true;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            trace.Disposing.TrySetResult();
            await trace.Continue.Task;
            trace.Events.Add("dispose:second");
            if (trace.Failure == "disposal") throw new InvalidOperationException("Disposal failed.");
        }
    }

    /// <summary>
    /// Owns a real isolated document and its migration backups.
    /// </summary>
    private sealed class SettingsFile : IDisposable
    {
        /// <summary>
        /// Gets the isolated test directory.
        /// </summary>
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Everywhere.Settings.Migrations", Guid.NewGuid().ToString("N"));

        /// <summary>
        /// Gets the settings path.
        /// </summary>
        public string Path => System.IO.Path.Combine(Directory, "settings.json");

        /// <summary>
        /// Gets the exact original contents when a file existed.
        /// </summary>
        public string? Original { get; }

        /// <summary>
        /// Creates the directory and optionally a previous-version document.
        /// </summary>
        public SettingsFile(string? original)
        {
            Original = original;
            System.IO.Directory.CreateDirectory(Directory);
            if (original is not null) File.WriteAllText(Path, original);
        }

        /// <inheritdoc />
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}