using System.Text.Json.Nodes;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.Configuration;

public sealed class SettingsMigratorTests
{
    private string _directory = string.Empty;
    private string _settingsPath = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Everywhere.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void Migrate_WhenVersionsAlreadyApplied_DoesNotResolveMigrationsOrDependencies()
    {
        const string original = """{"Version":"2.0.0"}""";
        File.WriteAllText(_settingsPath, original);
        using var services = new ServiceCollection()
            .AddTransient<FirstMigration>(_ => throw new InvalidOperationException("Migration must not be constructed."))
            .AddTransient<MigrationTrace>(_ => throw new InvalidOperationException("Dependency must not be constructed."))
            .AddSettingsMigration<FirstMigration>()
            .AddSettingsMigration<SecondMigration>()
            .BuildServiceProvider();

        new SettingsMigrator(_settingsPath, services, NullLogger.Instance).Migrate();

        Assert.That(File.ReadAllText(_settingsPath), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
    }

    [Test]
    public void Migrate_WithPendingMigrations_UsesDiFactoriesInVersionOrderAndDisposesInstances()
    {
        File.WriteAllText(_settingsPath, """{"Version":"0.0.0"}""");
        var trace = new MigrationTrace();

        using (var services = new ServiceCollection()
                   .AddSingleton(trace)
                   .AddTransient<FirstMigration>(provider =>
                   {
                       trace.Events.Add("factory:first");
                       return new FirstMigration(provider.GetRequiredService<MigrationTrace>());
                   })
                   .AddSettingsMigration<SecondMigration>()
                   .AddSettingsMigration<FirstMigration>()
                   .BuildServiceProvider())
        {
            new SettingsMigrator(_settingsPath, services, NullLogger.Instance).Migrate();

            Assert.That(trace.Events, Is.EqualTo(new[] { "factory:first", "run:first", "construct:second", "run:second" }));
            var root = JsonNode.Parse(File.ReadAllText(_settingsPath));
            Assert.That(root?["Version"]?.GetValue<string>(), Is.EqualTo("2.0.0"));
            Assert.That(root?["Value"]?.GetValue<string>(), Is.EqualTo("second"));
        }

        Assert.That(trace.Events.TakeLast(2), Is.EqualTo(new[] { "dispose:second", "dispose:first" }));
    }

    [Test]
    public void Migrate_WhenMigrationFails_DoesNotResolveLaterMigrationOrWriteSettings()
    {
        const string original = """{"Version":"0.0.0"}""";
        File.WriteAllText(_settingsPath, original);
        var trace = new MigrationTrace { ShouldFail = true };
        using var services = new ServiceCollection()
            .AddSingleton(trace)
            .AddSettingsMigration<SecondMigration>()
            .AddSettingsMigration<FirstMigration>()
            .BuildServiceProvider();
        var migrator = new SettingsMigrator(_settingsPath, services, NullLogger.Instance);

        Assert.Throws<InvalidOperationException>(migrator.Migrate);

        Assert.That(trace.Events, Is.EqualTo(new[] { "run:first" }));
        Assert.That(File.ReadAllText(_settingsPath), Is.EqualTo(original));
    }

    /// <summary>
    /// Existing Canary documents select the same later migrations and persist the final version.
    /// </summary>
    [Test]
    public void Migrate_HistoricalCanaryDocument_PreservesPendingMigrationOrder()
    {
        File.WriteAllText(_settingsPath, """{"Version":"0.8.1-canary.20260702.12"}""");
        var versions = new[]
        {
            "0.5.6", "0.5.9", "0.6.6", "0.7.0", "0.8.0", "0.8.1-canary.20260629.12",
            "0.8.1-canary.20260702.12", "0.8.1-canary.20260712.12", "0.8.1-canary.20260721.14", "0.9.0-canary.20261001.24"
        };
        var applied = new List<string>();
        var registrations = new ServiceCollection();
        foreach (var text in versions.Reverse())
        {
            var version = SemanticVersion.Parse(text) ?? throw new AssertionException(text);
            registrations.AddSingleton(new SettingsMigrationRegistration(version, _ => new RecordedMigration(text, applied)));
        }

        using var services = registrations.BuildServiceProvider();
        var migrator = new SettingsMigrator(_settingsPath, services, NullLogger.Instance);
        migrator.Migrate();

        Assert.That(applied, Is.EqualTo(versions.Skip(7)));
        Assert.That(JsonNode.Parse(File.ReadAllText(_settingsPath))?["Version"]?.GetValue<string>(), Is.EqualTo(versions[^1]));
        applied.Clear();
        migrator.Migrate();
        Assert.That(applied, Is.Empty);
    }

    /// <summary>
    /// Records a selected migration without depending on unrelated historical JSON transformations.
    /// </summary>
    private sealed class RecordedMigration(string version, List<string> applied) : SettingsMigration
    {
        /// <inheritdoc />
        protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [Apply];

        /// <summary>
        /// Records the order observed by the production migrator.
        /// </summary>
        private bool Apply(JsonObject root)
        {
            applied.Add(version);
            return false;
        }
    }

    /// <summary>
    /// Records activation and disposal order without using shared static test state.
    /// </summary>
    private sealed class MigrationTrace
    {
        public List<string> Events { get; } = [];

        public bool ShouldFail { get; init; }
    }

    /// <summary>
    /// Changes the in-memory document before optionally failing, and participates in DI disposal.
    /// </summary>
    private sealed class FirstMigration(MigrationTrace trace) : SettingsMigration, ISettingsMigrationMetadata, IDisposable
    {
        /// <inheritdoc />
        public static SemanticVersion Version { get; } = new(1);

        /// <inheritdoc />
        protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [Apply];

        private bool Apply(JsonObject root)
        {
            trace.Events.Add("run:first");
            root["Value"] = "first";
            if (trace.ShouldFail) throw new InvalidOperationException("Migration failed.");

            return true;
        }

        /// <inheritdoc />
        public void Dispose() => trace.Events.Add("dispose:first");
    }

    /// <summary>
    /// Records whether DI constructs a later migration before the earlier migration finishes.
    /// </summary>
    private sealed class SecondMigration : SettingsMigration, ISettingsMigrationMetadata, IDisposable
    {
        /// <inheritdoc />
        public static SemanticVersion Version { get; } = new(2);

        /// <inheritdoc />
        protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [Apply];

        private readonly MigrationTrace _trace;

        public SecondMigration(MigrationTrace trace)
        {
            _trace = trace;
            trace.Events.Add("construct:second");
        }

        private bool Apply(JsonObject root)
        {
            _trace.Events.Add("run:second");
            root["Value"] = "second";
            return true;
        }

        /// <inheritdoc />
        public void Dispose() => _trace.Events.Add("dispose:second");
    }
}