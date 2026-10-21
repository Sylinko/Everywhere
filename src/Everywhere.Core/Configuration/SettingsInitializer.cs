using System.Text.Json.Nodes;
using Everywhere.AI.Prompts.Database;
using Everywhere.Common;
using Everywhere.Configuration.Engine;
using Everywhere.Configuration.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Configuration;

/// <summary>
/// Initializes the application settings document, built-in migrations and runtime <see cref="Settings"/> model.
/// </summary>
/// <remarks>
/// This application-owned initializer prepares the built-in document and owns its
/// <see cref="SettingsEngine{TSettings}"/> until shutdown. Mods use the separate
/// context settings extension rather than resolving this initializer.
/// </remarks>
internal sealed class SettingsInitializer : IAsyncInitializer, IDisposable
{
    private readonly string _filePath;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SettingsPatchBinder _binder;

    private SettingsEngine<Settings>? _engine;
    private JsonSettingsStorage? _storage;

    /// <summary>
    /// Gets the runtime settings object used by the rest of the application.
    /// </summary>
    public Settings Settings { get; }

    /// <summary>
    /// Gets the JSON document store used for persistence.
    /// </summary>
    public JsonSettingsStorage Storage => _storage ?? throw new InvalidOperationException("Application settings have not been initialized.");

    /// <summary>
    /// Gets diagnostics reported by the document store and patch binder.
    /// </summary>
    public IEnumerable<SettingsEngineDiagnostic> Diagnostics => (_storage?.Diagnostics ?? []).Concat(_binder.Diagnostics);

    /// <summary>
    /// Creates the application initializer over the singleton runtime settings object.
    /// </summary>
    public SettingsInitializer(
        Settings settings,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory
    ) : this(
        settings,
        Path.Combine(RuntimeConstants.WritableFolderPath, "settings.json"),
        serviceProvider,
        loggerFactory)
    {
    }

    /// <summary>
    /// Creates the application initializer with an explicit document path.
    /// </summary>
    internal SettingsInitializer(Settings settings, string filePath, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        Settings = settings;
        _filePath = filePath;
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _binder = new SettingsPatchBinder();
    }

    #region IAsyncInitializer

    /// <inheritdoc />
    static AsyncInitializerIndex IAsyncInitializer.Index => AsyncInitializerIndex.Settings;

    /// <inheritdoc />
    Task IAsyncInitializer.InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunMigrations();

        _storage = JsonSettingsStorage.Load(_filePath, _loggerFactory.CreateLogger<JsonSettingsStorage>());
        try
        {
            _engine = new SettingsEngine<Settings>(Settings, _storage, _binder);
        }
        catch
        {
            _storage.Dispose();
            _storage = null;
            throw;
        }

        return Task.CompletedTask;
    }

    #endregion

    /// <summary>
    /// Stops observing runtime settings and releases the JSON document store.
    /// </summary>
    public void Dispose()
    {
        if (_engine is not { } engine) return;

        try
        {
            // Dispose can run before the debounced save loop reaches its final
            // tick, so force the last in-memory snapshot to disk first.
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<SettingsInitializer>().LogError(ex, "Failed to flush settings during shutdown.");
        }
    }

    private void RunMigrations()
    {
        try
        {
            using var migrationProvider = BuildMigrationProvider();
            var migrator = new SettingsMigrator(
                _filePath,
                migrationProvider,
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

        services.AddSettingsMigration<_20260103124001_0_5_6>();
        services.AddSettingsMigration<_20260106195452_0_5_9>();
        services.AddSettingsMigration<_20260106195452_0_6_6>();
        services.AddSettingsMigration<_20260208160256_0_7_0>();
        services.AddSettingsMigration<_20260614154350_0_8_0>();
        services.AddSettingsMigration<_20260629120000_0_8_1_canary_20260629_12>();
        services.AddSettingsMigration<_20260712010000_0_8_1_canary_20260712_12>();

        if (_serviceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IDbContextFactory<PromptDbContext>)))
        {
            // Resolve the root-owned database factory only when this migration is selected. Passing it directly
            // keeps the temporary container from capturing and disposing a dependency owned by the root provider.
            services.AddTransient(provider =>
                new _20260702120000_0_8_1_canary_20260702_12(
                    _serviceProvider.GetRequiredService<IDbContextFactory<PromptDbContext>>(),
                    provider.GetRequiredService<ILogger<_20260702120000_0_8_1_canary_20260702_12>>()));
            services.AddSettingsMigration<_20260702120000_0_8_1_canary_20260702_12>();
        }

        services.AddSettingsMigration<_20260721120000_0_8_1_canary_20260721_14>();
        services.AddSettingsMigration<_20261001023708_0_9_0_canary_20261001_24>();

        return services.BuildServiceProvider();
    }

    private void ValidateMigratedSettings(JsonObject root)
    {
        var binder = new SettingsPatchBinder();
        root.Remove(SettingsEngineJson.VersionPropertyName);
        binder.Patch(root, new Settings(_serviceProvider));

        var failures = binder.Diagnostics
            .AsValueEnumerable()
            .Where(static diagnostic => diagnostic.Severity >= SettingsEngineDiagnosticSeverity.Error)
            .Select(static diagnostic => $"{diagnostic.Kind} at '{diagnostic.Path}'")
            .ToArray();

        if (failures.Length > 0)
        {
            throw new InvalidDataException($"Migrated settings failed validation: {string.Join("; ", failures)}");
        }
    }
}