using System.Text.Json.Nodes;
using Everywhere.Common;
using Everywhere.Views;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// Infers legacy onboarding completion when migrating settings created before
/// <see cref="Settings.CompletedOobeVersion"/> existed.
/// </summary>
public sealed class _20261123162000_0_9_0_canary_20261123_29 : SettingsMigration, ISettingsMigrationMetadata
{
    /// <inheritdoc />
    public static SemanticVersion Version { get; } = new(0, 9, 0, 0, "canary.20261123.29");

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [MigrateLegacyOobeCompletion];

    private static bool MigrateLegacyOobeCompletion(JsonObject root)
    {
        if (root.ContainsKey(nameof(Settings.CompletedOobeVersion)))
        {
            return false;
        }

        if (GetPathNode(root, "Model.CustomAssistants") is not JsonArray { Count: > 0 })
        {
            return false;
        }

        root[nameof(Settings.CompletedOobeVersion)] = OobeCoordinator.LegacyBaselineVersion;
        return true;
    }
}
