using System.Text.Json.Nodes;
using Everywhere.AI.Prompts.Database;
using Everywhere.Common;
using Everywhere.Configuration.Migrations;
using Everywhere.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Configuration.Engine;

/// <summary>
/// Coordinates the JSON-backed settings document and the runtime <see cref="Configuration.Settings"/> object.
/// </summary>
/// <remarks>
/// SettingsEngine is a JSON document patch engine, not an <c>IConfigurationProvider</c>.
/// The JSON document remains the persistence source of truth, while the runtime
/// object graph is patched in place so MVVM references stay stable.
/// </remarks>
public sealed class SettingsEngine : IAsyncInitializer
{
    private readonly string _filePath;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SettingsPatchBinder _binder;
    private readonly ISettingsDescriptor _rootDescriptor;
    private readonly ObjectObserver _observer;
    private JsonSettingsStorage? _storage;

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Settings;

    /// <summary>
    /// Gets the runtime settings object used by the rest of the application.
    /// </summary>
    public Settings Settings { get; }

    /// <summary>
    /// Gets the JSON document store used for persistence.
    /// </summary>
    public JsonSettingsStorage Storage =>
        _storage ?? throw new InvalidOperationException("SettingsEngine has not been initialized.");

    /// <summary>
    /// Gets diagnostics reported by the document store and patch binder.
    /// </summary>
    public IEnumerable<SettingsEngineDiagnostic> Diagnostics =>
        (_storage?.Diagnostics ?? []).Concat(_binder.Diagnostics);

    /// <summary>
    /// Creates a settings engine over the singleton runtime settings object.
    /// </summary>
    public SettingsEngine(Settings settings, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        : this(
            settings,
            Path.Combine(RuntimeConstants.WritableFolderPath, "settings.json"),
            serviceProvider,
            loggerFactory)
    {
    }

    /// <summary>
    /// Creates a settings engine over an existing runtime settings object and settings file.
    /// </summary>
    internal SettingsEngine(Settings settings, string filePath, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        Settings = settings;
        _filePath = filePath;
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;

        _binder = new SettingsPatchBinder(serviceProvider);
        _rootDescriptor = _binder.GetDescriptor(typeof(Settings));
        _observer = new ObjectObserver(HandleSettingsChanges);
    }

    public Task InitializeAsync()
    {
        RunMigrations();

        _storage = JsonSettingsStorage.Load(_filePath, _loggerFactory.CreateLogger<JsonSettingsStorage>());
        PatchSettings();
        StartObserveSettings();

        return Task.CompletedTask;
    }

    private void HandleSettingsChanges(in ObjectObserverChangedEventArgs e)
    {
        _binder.WriteObservedPath(Storage, _rootDescriptor, e.Path, e.Value);
    }

    private void RunMigrations()
    {
        try
        {
            using var migrationProvider = BuildMigrationProvider();
            var migrations = migrationProvider.GetServices<SettingsMigration>();

            var migrator = new SettingsMigrator(
                _filePath,
                migrations,
                _loggerFactory.CreateLogger<SettingsMigrator>(),
                ValidateMigratedSettings);
            migrator.Migrate();
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger("SettingsMigration").LogError(ex, "Error running settings migrations");
            throw;
        }
    }

    /// <summary>
    /// Builds a short-lived container that owns only settings migration construction.
    /// </summary>
    /// <remarks>
    /// Migrations are registered explicitly so they stay strongly referenced and trim-friendly.
    /// The application root provider supplies only already-registered infrastructure dependencies;
    /// migrations themselves are not added to the application-wide DI graph.
    /// </remarks>
    private ServiceProvider BuildMigrationProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_loggerFactory);
        services.AddLogging();

        services.AddTransient<SettingsMigration, _20260103124001_0_5_6>();
        services.AddTransient<SettingsMigration, _20260106195452_0_5_9>();
        services.AddTransient<SettingsMigration, _20260106195452_0_6_6>();
        services.AddTransient<SettingsMigration, _20260208160256_0_7_0>();
        services.AddTransient<SettingsMigration, _20260614154350_0_8_0>();
        services.AddTransient<SettingsMigration, _20260629120000_0_8_1_canary_20260629_12>();
        if (_serviceProvider.GetService<IDbContextFactory<PromptDbContext>>() is { } promptDbFactory)
        {
            services.AddSingleton(promptDbFactory);
            services.AddTransient<SettingsMigration, _20260702120000_0_8_1_canary_20260702_12>();
        }

        return services.BuildServiceProvider();
    }

    private void ValidateMigratedSettings(JsonObject root)
    {
        var binder = new SettingsPatchBinder(_serviceProvider);
        binder.Patch(root, new Settings(_serviceProvider));

        var failures = binder.Diagnostics
            .AsValueEnumerable()
            .Where(static diagnostic => diagnostic.Severity != SettingsEngineDiagnosticSeverity.Info)
            .Select(static diagnostic => $"{diagnostic.Kind} at '{diagnostic.Path}'")
            .ToList();

        if (failures.Count > 0)
        {
            throw new InvalidDataException($"Migrated settings failed validation: {string.Join("; ", failures)}");
        }
    }

    /// <summary>
    /// Patches the runtime settings object from the current JSON document.
    /// </summary>
    private void PatchSettings() => Storage.Edit(root => _binder.Patch(root, Settings), signalChange: false);

    /// <summary>
    /// Starts observing the runtime settings object for changes and writing them to the JSON document.
    /// </summary>
    private void StartObserveSettings() => _observer.Observe(Settings);
}
