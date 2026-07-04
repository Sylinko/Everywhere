using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Common;
using Everywhere.Configuration.Engine;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Configuration;

/// <summary>
/// Applies versioned JSON migrations to the settings document before SettingsEngine reads it.
/// </summary>
/// <remarks>
/// Migrations run against an in-memory <see cref="JsonObject"/> and are committed
/// only after all pending migrations and the optional validator succeed. This
/// keeps a broken migration from partially overwriting the user's settings file.
/// </remarks>
public class SettingsMigrator
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly IEnumerable<SettingsMigration> _migrations;
    private readonly Action<JsonObject>? _validateBeforeSave;

    public SettingsMigrator(
        string filePath,
        IEnumerable<SettingsMigration> migrations,
        ILogger logger,
        Action<JsonObject>? validateBeforeSave = null)
    {
        _filePath = filePath;
        _migrations = migrations.AsValueEnumerable().OrderBy(m => m.Version).ToList();
        _logger = logger;
        _validateBeforeSave = validateBeforeSave;

        // migrations must have unique versions
        Debug.Assert(
            _migrations.AsValueEnumerable().Select(m => m.Version).Distinct().Count() == _migrations.Count(),
            "Settings migrations must have unique versions");
    }

    /// <summary>
    /// Runs all migrations newer than the current <c>Settings.Version</c>.
    /// </summary>
    /// <exception cref="Exception">
    /// Any parse, migration, validation, backup, or write failure is allowed to
    /// escape so startup can stop before loading an untrusted settings file.
    /// </exception>
    public void Migrate()
    {
        var fileExists = File.Exists(_filePath);
        var root = ReadRoot(fileExists);

        var currentVersionStr = root[nameof(Settings.Version)]?.GetValue<string>();
        var currentVersion = SemanticVersion.TryParse(currentVersionStr, out var version) ? version : new SemanticVersion(0);
        var originalVersion = currentVersion;
        var hasChanges = false;
        var pendingMigrations = _migrations.AsValueEnumerable().Where(m => m.Version > originalVersion).ToList();

        if (pendingMigrations.Count == 0)
        {
            return;
        }

        if (fileExists)
        {
            var backupPath = CreateBackup();
            _logger.LogInformation("Created settings migration backup: {BackupPath}", backupPath);
        }

        foreach (var migration in pendingMigrations)
        {
            _logger.LogInformation("Applying migration {Version}: {Name}", migration.Version, migration.GetType().Name);
            if (migration.Migrate(root)) hasChanges = true;
            currentVersion = migration.Version;
        }

        if (currentVersion > originalVersion || hasChanges)
        {
            root[nameof(Settings.Version)] = currentVersion.ToString();
            _validateBeforeSave?.Invoke((JsonObject)root.DeepClone());
            WriteAtomically(root);
        }
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

    private void WriteAtomically(JsonObject root)
    {
        var directory = Path.GetDirectoryName(_filePath);
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

            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save migrated settings: {FilePath}", _filePath);
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
                _logger.LogWarning(ex, "Failed to delete temporary migration file: {TempPath}", tempPath);
            }
        }
    }
}