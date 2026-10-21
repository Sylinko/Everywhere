using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Common;
using Everywhere.Configuration.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Configuration;

/// <summary>
/// Applies versioned JSON migrations to the settings document before SettingsEngine reads it.
/// </summary>
/// <remarks>
/// Static registration metadata is read before selecting migrations. Only pending migrations are resolved,
/// in version order, through the supplied provider; the caller owns that provider and disposes it afterward.
/// Migrations run against an in-memory <see cref="JsonObject"/> and are committed
/// only after all pending migrations and the optional validator succeed. This
/// keeps a broken migration from partially overwriting the user's settings file.
/// </remarks>
public class SettingsMigrator
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly IServiceProvider _services;
    private readonly SettingsMigrationRegistration[] _migrations;
    private readonly Action<JsonObject>? _validateBeforeSave;

    public SettingsMigrator(
        string filePath,
        IServiceProvider services,
        ILogger logger,
        Action<JsonObject>? validateBeforeSave = null)
    {
        _filePath = filePath;
        _services = services;
        _migrations = services.GetServices<SettingsMigrationRegistration>().AsValueEnumerable().OrderBy(m => m.Version).ToArray();
        _logger = logger;
        _validateBeforeSave = validateBeforeSave;

        if (_migrations.AsValueEnumerable().Select(m => m.Version).Distinct().Count() != _migrations.Length)
        {
            throw new InvalidOperationException("Settings migrations must have unique versions.");
        }
    }

    /// <summary>
    /// Runs all migrations newer than the settings document's <c>Version</c> metadata.
    /// </summary>
    /// <exception cref="Exception">
    /// Any parse, migration, validation, backup, or write failure is allowed to
    /// escape so startup can stop before loading an untrusted settings file.
    /// </exception>
    public void Migrate()
    {
        if (PrepareMigration() is { } root) WriteAtomically(_filePath, _logger, root);
    }

    /// <summary>
    /// Initializes an independently versioned document and releases migration resources before committing.
    /// Registrations and validators are borrowed only for this operation, never retained by the settings engine.
    /// </summary>
    public static async Task InitializeAsync(
        string filePath,
        SemanticVersion targetVersion,
        Func<JsonObject> createDefaults,
        Action<JsonObject> validate,
        Action<IServiceCollection>? configureMigrations,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registrations = new ServiceCollection();
        registrations.AddSingleton(loggerFactory);
        registrations.AddLogging();
        configureMigrations?.Invoke(registrations);
        var logger = loggerFactory.CreateLogger<SettingsMigrator>();
        var document = await PrepareAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (document is not null) WriteAtomically(filePath, logger, document);

        async Task<JsonObject?> PrepareAsync()
        {
            await using var provider = registrations.BuildServiceProvider();
            var migrator = new SettingsMigrator(filePath, provider, logger, validate);
            return migrator.PrepareMigration(targetVersion, createDefaults);
        }
    }

    /// <summary>
    /// Prepares a document without committing it so the caller can finish resource cleanup first.
    /// A null target preserves the application's existing migration-version policy.
    /// </summary>
    private JsonObject? PrepareMigration(SemanticVersion? targetVersion = null, Func<JsonObject>? createDefaults = null)
    {
        var fileExists = File.Exists(_filePath);
        var root = !fileExists && createDefaults is not null ? createDefaults() : ReadRoot(fileExists);
        if (targetVersion is not null)
        {
            if (_migrations.AsValueEnumerable().Any(m => m.Version > targetVersion))
            {
                throw new InvalidDataException("Migration targets must not exceed the declared settings version.");
            }

            if (!fileExists)
            {
                root[SettingsEngineJson.VersionPropertyName] = targetVersion.ToString();
                _validateBeforeSave?.Invoke((JsonObject)root.DeepClone());
                return root;
            }
        }

        var currentVersionStr = root[SettingsEngineJson.VersionPropertyName]?.GetValue<string>();
        var currentVersion = SemanticVersion.TryParse(currentVersionStr, out var version) ? version : new SemanticVersion(0);
        if (targetVersion is not null)
        {
            if (root.ContainsKey(SettingsEngineJson.VersionPropertyName) && !SemanticVersion.TryParse(currentVersionStr, out _))
            {
                throw new InvalidDataException("The settings document has an invalid Version.");
            }

            if (currentVersion > targetVersion)
            {
                throw new InvalidDataException($"Settings version {currentVersion} is newer than supported version {targetVersion}.");
            }

            if (currentVersion < targetVersion && _migrations.AsValueEnumerable().LastOrDefault()?.Version != targetVersion)
            {
                throw new InvalidDataException($"No migration reaches settings version {targetVersion}.");
            }
        }

        var originalVersion = currentVersion;
        var hasChanges = false;
        var pendingMigrations = _migrations.AsValueEnumerable().Where(m => m.Version > originalVersion).ToArray();
        if (pendingMigrations.Length == 0)
        {
            if (targetVersion is not null) _validateBeforeSave?.Invoke((JsonObject)root.DeepClone());
            return null;
        }

        if (fileExists)
        {
            var backupPath = CreateBackup();
            _logger.LogInformation("Created settings migration backup: {BackupPath}", backupPath);
        }

        foreach (var registration in pendingMigrations)
        {
            var migration = registration.Resolve(_services);
            _logger.LogInformation("Applying migration {Version}: {Name}", registration.Version, migration.GetType().Name);
            if (migration.Migrate(root)) hasChanges = true;
            currentVersion = registration.Version;
        }

        if (currentVersion > originalVersion || hasChanges)
        {
            root[SettingsEngineJson.VersionPropertyName] = currentVersion.ToString();
            _validateBeforeSave?.Invoke((JsonObject)root.DeepClone());
            return root;
        }

        return null;
    }

    private JsonObject ReadRoot(bool fileExists)
    {
        if (!fileExists)
        {
            return new JsonObject();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonNode.Parse(json, null, SettingsEngineJson.DocumentOptions) as JsonObject ??
                throw new InvalidDataException($"Settings file must contain a JSON object: {_filePath}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ex = HandledSystemException.Handle(ex);
            _logger.LogError(ex, "Failed to parse settings file for migration: {FilePath}", _filePath);

            if (File.Exists(_filePath))
            {
                var backupPath = CreateBackup();
                _logger.LogInformation("Created backup for unreadable settings file: {BackupPath}", backupPath);
            }

            ExceptionDispatchInfo.Capture(ex).Throw();
            throw;
        }
    }

    private string CreateBackup()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var extension = Path.GetExtension(_filePath);
        var name = Path.GetFileNameWithoutExtension(_filePath);
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var backupPath = Path.Combine(directory ?? string.Empty, $"{name}_backup_{timestamp}{extension}");
        var attempt = 0;

        while (File.Exists(backupPath))
        {
            attempt++;
            backupPath = Path.Combine(directory ?? string.Empty, $"{name}_backup_{timestamp}_{attempt}{extension}");
        }

        File.Copy(_filePath, backupPath);
        return backupPath;
    }

    /// <summary>
    /// Replaces the document only after migration, validation and temporary provider disposal have succeeded.
    /// </summary>
    private static void WriteAtomically(string filePath, ILogger logger, JsonObject root)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? string.Empty, Path.GetRandomFileName());
        try
        {
            var content = root.ToJsonString(SettingsEngineJson.SerializerOptions);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                var buffer = Encoding.UTF8.GetBytes(content);
                stream.Write(buffer);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save migrated settings: {FilePath}", filePath);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete temporary migration file: {TempPath}", tempPath);
            }
        }
    }
}