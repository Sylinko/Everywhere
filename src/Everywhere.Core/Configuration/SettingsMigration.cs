using System.Text.Json.Nodes;
using Everywhere.Common;
using Serilog;

namespace Everywhere.Configuration;

/// <summary>
/// The base class for settings migrations.
/// </summary>
public abstract class SettingsMigration
{
    /// <summary>
    /// The target version of the migration.
    /// </summary>
    public abstract SemanticVersion Version { get; }

    /// <summary>
    /// The list of migration tasks to be performed.
    /// </summary>
    protected abstract IEnumerable<Func<JsonObject, bool>> MigrationTasks { get; }

    /// <summary>
    /// Performs the migration on the given JSON root object.
    /// </summary>
    /// <param name="root"></param>
    /// <returns>true if the migration made changes; otherwise, false.</returns>
    internal bool Migrate(JsonObject root)
    {
        var modified = false;
        foreach (var task in MigrationTasks)
        {
            try
            {
                modified |= task(root);
            }
            catch
            {
                // Ignore individual task errors to allow other tasks to run
                Log.Warning("Migration task in {Migration} failed for version {Version}", GetType().Name, Version);
            }
        }
        return modified;
    }

    /// <summary>
    /// Helper to get a JsonNode value by a dot-separated path.
    /// </summary>
    /// <param name="root"></param>
    /// <param name="path"></param>
    /// <returns></returns>
    protected static JsonNode? GetPathNode(JsonObject root, string path)
    {
        var ranges = path.AsSpan().Split('.');
        JsonNode? currentNode = root;

        foreach (var range in ranges)
        {
            var segment = path[range];
            if (currentNode is not JsonObject currentObj || !currentObj.TryGetPropertyValue(segment, out currentNode))
            {
                return null;
            }
        }

        return currentNode;
    }

    /// <summary>
    /// Helper to flatten a Customizable{T} object structure to its value.
    /// Looks for "CustomValue" or "DefaultValue" and replaces the object with the value.
    /// </summary>
    protected static bool FlattenCustomizable(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var propertyNode) || propertyNode is not JsonObject customObj)
        {
            return false;
        }

        JsonNode? valueToKeep = null;

        // Check for CustomValue first
        if (customObj.TryGetPropertyValue("CustomValue", out var customValue) && customValue is not null)
        {
            valueToKeep = customValue;
        }
        // Fallback to DefaultValue
        else if (customObj.TryGetPropertyValue("DefaultValue", out var defaultValue))
        {
            valueToKeep = defaultValue;
        }

        if (valueToKeep != null)
        {
            // We must clone the node because it's attached to the old parent
            var newValue = valueToKeep.DeepClone();
            obj[propertyName] = newValue;
            return true;
        }

        obj[propertyName] = null;
        return true;
    }

    /// <summary>
    /// Helper to move a property from source path to destination path.
    /// </summary>
    /// <param name="root"></param>
    /// <param name="sourcePath"></param>
    /// <param name="destinationPath"></param>
    /// <returns></returns>
    /// <example>
    /// TryMoveProperty(root, "Common.Proxy", "Proxy")
    /// </example>
    /// <example>
    /// TryMoveProperty(root, "ChatWindow.Shortcut", "Shortcut.ChatWindow")
    /// </example>
    protected static bool TryMoveProperty(JsonObject root, string sourcePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }

        if (sourcePath == destinationPath)
        {
            return false;
        }

        var srcSegments = sourcePath.Split('.');
        var currentSrcParent = root;

        for (var i = 0; i < srcSegments.Length - 1; i++)
        {
            if (!currentSrcParent.TryGetPropertyValue(srcSegments[i], out var nextNode) || nextNode is not JsonObject nextObj)
            {
                return false; // Source is not found
            }

            currentSrcParent = nextObj;
        }

        var srcProp = srcSegments[^1];
        if (!currentSrcParent.ContainsKey(srcProp))
        {
            return false; // Source is not found
        }

        var nodeToMove = currentSrcParent[srcProp];
        currentSrcParent.Remove(srcProp);

        var destSegments = destinationPath.Split('.');
        var currentDestParent = root;
        for (var i = 0; i < destSegments.Length - 1; i++)
        {
            var segment = destSegments[i];
            if (currentDestParent.TryGetPropertyValue(segment, out var existingNode) && existingNode is JsonObject existingObj)
            {
                currentDestParent = existingObj;
            }
            else
            {
                var newObject = new JsonObject();
                currentDestParent[segment] = newObject;
                currentDestParent = newObject;
            }
        }

        var destProperty = destSegments[^1];
        currentDestParent[destProperty] = nodeToMove;

        return true;
    }
}
