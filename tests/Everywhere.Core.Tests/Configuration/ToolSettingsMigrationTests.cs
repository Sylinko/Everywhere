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
        var root = JsonNode.Parse(
            $$"""
              {
                "Plugin": {
                  "IsEnabledRecords": {
                    "builtin.visual_context": false,
                    "builtin.file_system.read_file": false,
                    "mcp.{{mcpId}}.search": true
                  },
                  "IsPermissionGrantedRecords": {
                    "builtin.file_system.write_to_file": true,
                    "builtin.file_system.write_to_file.|C:\\Source\\Everywhere\\**": true,
                    "builtin.file_system.write_to_file.overwrite": true
                  },
                  "Terminal": {
                    "AutoApprove": true
                  }
                }
              }
              """)!.AsObject();

        var modified = new _20260721120000_0_8_1_canary_20260721_14().Migrate(root);
        var plugin = root["Plugin"]!.AsObject();
        var enablement = plugin["ToolEnablementRulesets"]!.AsObject();
        var bypassApprovalRulesets = plugin["ToolBypassApprovalRulesets"]!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(modified, Is.True);
            Assert.That(plugin.ContainsKey("IsEnabledRecords"), Is.False);
            Assert.That(plugin.ContainsKey("IsPermissionGrantedRecords"), Is.False);
            Assert.That(plugin["Terminal"]!["BypassesApproval"]!.GetValue<bool>(), Is.True);
            Assert.That(plugin["Terminal"]!.AsObject().ContainsKey("AutoApprove"), Is.False);
            Assert.That(enablement[ToolSettingsKey.ForPlugin("builtin.visual_context")]!.GetValue<bool>(), Is.False);
            Assert.That(enablement[ToolSettingsKey.ForFunction("builtin.file_system", "read_file")]!.GetValue<bool>(), Is.False);
            Assert.That(enablement[ToolSettingsKey.ForFunction($"mcp.{mcpId}", "search")]!.GetValue<bool>(), Is.True);
            Assert.That(bypassApprovalRulesets[ToolSettingsKey.ForFunction("builtin.file_system", "write_to_file")]!.GetValue<bool>(), Is.True);
            Assert.That(
                bypassApprovalRulesets.Any(pair => pair.Key.StartsWith(
                    $"{ToolSettingsKey.ForFunction("builtin.file_system", "write_to_file")}/permission/|",
                    StringComparison.OrdinalIgnoreCase)),
                Is.False);
            Assert.That(
                bypassApprovalRulesets[$"{ToolSettingsKey.ForFunction("builtin.file_system", "write_to_file")}/permission/overwrite"]!
                    .GetValue<bool>(),
                Is.True);
        });
    }

    [Test]
    public void Migrate_LegacyBuiltinEverythingRulesets_RewritesToModEverythingWithoutOverwritingExisting()
    {
        var root = JsonNode.Parse(
            $$"""
              {
                "Plugin": {
                  "ToolEnablementRulesets": {
                    "{{ToolSettingsKey.ForPlugin("builtin.everything")}}": false,
                    "{{ToolSettingsKey.ForFunction("builtin.everything", "search_files")}}": true,
                    "{{ToolSettingsKey.ForPlugin("builtin.visual_context")}}": true
                  },
                  "ToolBypassApprovalRulesets": {
                    "{{ToolSettingsKey.ForFunction("builtin.everything", "search_files")}}/permission/custom": true
                  }
                },
                "Model": {
                  "CustomAssistants": [
                    {
                      "ToolEnablementRulesets": {
                        "{{ToolSettingsKey.ForPlugin("builtin.everything")}}": true,
                        "{{ToolSettingsKey.ForPlugin("mod.everything")}}": false,
                        "{{ToolSettingsKey.ForFunction("builtin.everything", "search_files")}}": false
                      }
                    }
                  ]
                }
              }
              """)!.AsObject();

        var isModified = new _20261124180000_0_9_0_canary_20261124_31().Migrate(root);
        var pluginEnablement = root["Plugin"]!["ToolEnablementRulesets"]!.AsObject();
        var bypass = root["Plugin"]!["ToolBypassApprovalRulesets"]!.AsObject();
        var assistantEnablement = root["Model"]!["CustomAssistants"]![0]!["ToolEnablementRulesets"]!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(isModified, Is.True);
            Assert.That(pluginEnablement.ContainsKey(ToolSettingsKey.ForPlugin("builtin.everything")), Is.False);
            Assert.That(pluginEnablement[ToolSettingsKey.ForPlugin("mod.everything")]!.GetValue<bool>(), Is.False);
            Assert.That(pluginEnablement[ToolSettingsKey.ForFunction("mod.everything", "search_files")]!.GetValue<bool>(), Is.True);
            Assert.That(pluginEnablement[ToolSettingsKey.ForPlugin("builtin.visual_context")]!.GetValue<bool>(), Is.True);

            Assert.That(
                bypass.ContainsKey($"{ToolSettingsKey.ForFunction("builtin.everything", "search_files")}/permission/custom"),
                Is.False);
            Assert.That(
                bypass[$"{ToolSettingsKey.ForFunction("mod.everything", "search_files")}/permission/custom"]!.GetValue<bool>(),
                Is.True);

            Assert.That(assistantEnablement.ContainsKey(ToolSettingsKey.ForPlugin("builtin.everything")), Is.False);
            Assert.That(assistantEnablement[ToolSettingsKey.ForPlugin("mod.everything")]!.GetValue<bool>(), Is.False);
            Assert.That(assistantEnablement[ToolSettingsKey.ForFunction("mod.everything", "search_files")]!.GetValue<bool>(), Is.False);
        });
    }
}
