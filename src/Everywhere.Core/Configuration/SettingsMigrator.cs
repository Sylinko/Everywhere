using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Common;
using Microsoft.Extensions.Logging;
using WritableJsonConfiguration;
using ZLinq;

namespace Everywhere.Configuration;

public class SettingsMigrator
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly IEnumerable<SettingsMigration> _migrations;

    public SettingsMigrator(string filePath, IEnumerable<SettingsMigration> migrations, ILogger logger)
    {
        _filePath = filePath;
        _migrations = migrations.AsValueEnumerable().OrderBy(m => m.Version).ToList();
        _logger = logger;

        // migrations must have unique versions
        Debug.Assert(_migrations.AsValueEnumerable().Select(m => m.Version).Distinct().Count() == _migrations.Count(),
            "Settings migrations must have unique versions");
    }

    public void Migrate()
    {
        JsonObject? root = null;
        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                root = JsonNode.Parse(json, null, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    AllowDuplicateProperties = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) as JsonObject;
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex);
                _logger.LogError(ex, "Failed to parse settings file for migration: {FilePath}", _filePath);

                if (ex.InnerException is JsonException)
                {
                    // backup the old broken one
                    try
                    {
                        var backupPath = Path.Combine(
                            Path.GetDirectoryName(_filePath) ?? string.Empty,
                            $"{Path.GetFileNameWithoutExtension(_filePath)}_backup_{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(_filePath)}");
                        File.Copy(_filePath, backupPath, true);
                    }
                    catch (Exception ex1)
                    {
                        ex1 = HandledSystemException.Handle(ex1);
                        _logger.LogError(ex1, "Failed to backup broken settings file: {FilePath}", _filePath);
                    }
                }
            }
        }

        // Start with empty settings if file doesn't exist or failed to parse
        // So that Version can be set and migrations applied
        root ??= new JsonObject();

        var currentVersionStr = root[nameof(Settings.Version)]?.GetValue<string>();
        var currentVersion = SemanticVersion.TryParse(currentVersionStr, out var version) ? version : new SemanticVersion(0);
        var originalVersion = currentVersion;
        var hasChanges = false;

        foreach (var migration in _migrations.AsValueEnumerable())
        {
            if (migration.Version <= currentVersion) continue;

            _logger.LogInformation("Applying migration {Version}: {Name}", migration.Version, migration.GetType().Name);
            try
            {
                if (migration.Migrate(root)) hasChanges = true;
                currentVersion = migration.Version;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply migration {Version}", migration.Version);
                break;
            }
        }

        if (currentVersion > originalVersion || hasChanges)
        {
            root[nameof(Settings.Version)] = currentVersion.ToString();

            try
            {
                File.WriteAllText(_filePath, root.ToJsonString(WritableJsonConfigurationSource.DefaultJsonSerializerOptions));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save migrated settings");
            }
        }
    }
}