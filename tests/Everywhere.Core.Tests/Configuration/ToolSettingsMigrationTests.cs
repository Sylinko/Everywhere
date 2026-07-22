using System.Text.Json.Nodes;
using Everywhere.Chat.Plugins;
using Everywhere.Configuration.Migrations;

namespace Everywhere.Core.Tests.Configuration;

public class ToolSettingsMigrationTests
{
    [Test]
    public void Migrate_WithLegacyToolRecords_RenamesPropertiesAndCreatesTypedExactKeys()
    {
        var mcpId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var root = JsonNode.Parse($$"""
        {
          "Plugin": {
            "IsEnabledRecords": {
              "builtin.visual_context": false,
              "builtin.file_system.read_file": false,
              "mcp.{{mcpId}}.search": true
            },
            "IsPermissionGrantedRecords": {
              "builtin.file_system.write_to_file": true,
              "builtin.file_system.write_to_file.overwrite": true
            }
          }
        }
        """)!.AsObject();

        var modified = new _20260721120000_0_8_1_canary_20260721_14().Migrate(root);
        var plugin = root["Plugin"]!.AsObject();
        var enablement = plugin["ToolEnablement"]!.AsObject();
        var autoApproval = plugin["ToolAutoApproval"]!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(modified, Is.True);
            Assert.That(plugin.ContainsKey("IsEnabledRecords"), Is.False);
            Assert.That(plugin.ContainsKey("IsPermissionGrantedRecords"), Is.False);
            Assert.That(enablement[ToolSettingsKey.ForPlugin("builtin.visual_context")]!.GetValue<bool>(), Is.False);
            Assert.That(enablement[ToolSettingsKey.ForFunction("builtin.file_system", "read_file")]!.GetValue<bool>(), Is.False);
            Assert.That(enablement[ToolSettingsKey.ForFunction($"mcp.{mcpId}", "search")]!.GetValue<bool>(), Is.True);
            Assert.That(autoApproval[ToolSettingsKey.ForFunction("builtin.file_system", "write_to_file")]!.GetValue<bool>(), Is.True);
            Assert.That(autoApproval[$"{ToolSettingsKey.ForFunction("builtin.file_system", "write_to_file")}/permission/overwrite"]!.GetValue<bool>(), Is.True);
        });
    }
}
