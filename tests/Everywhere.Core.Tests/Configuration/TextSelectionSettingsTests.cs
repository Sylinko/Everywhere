using System.Text.Json.Nodes;
using Everywhere.Configuration.Migrations;

namespace Everywhere.Core.Tests.Configuration;

public sealed class TextSelectionSettingsTests
{
    [Test]
    public void Migrate_LegacySelection_MovesBehaviorWithoutMigratingHiddenRules()
    {
        var root = JsonNode.Parse("""
            {
              "ChatWindow": {
                "AutomaticallyAddTextSelection": true,
                "IsTextSelectionFullscreenExcluded": false,
                "TextSelectionExcludedApplications": "Code.exe",
                "TextSelectionCopyFallbackExcludedApplications": "terminal.exe"
              }
            }
            """)?.AsObject() ?? throw new InvalidOperationException();

        Assert.That(new _20261016110032_0_9_0_canary_20261016_27().Migrate(root), Is.True);
        var selection = root["TextSelection"]?.AsObject() ?? throw new InvalidOperationException();
        Assert.Multiple(() =>
        {
            Assert.That(selection["IsEnabled"]?.GetValue<bool>(), Is.True);
            Assert.That(selection["IsFullscreenExcluded"]?.GetValue<bool>(), Is.False);
            Assert.That(selection.ContainsKey("Windows"), Is.False);
            Assert.That(selection.ContainsKey("Mac"), Is.False);
            Assert.That(root["ChatWindow"]?["AutomaticallyAddTextSelection"], Is.Null);
            Assert.That(root["ChatWindow"]?["IsTextSelectionFullscreenExcluded"], Is.Null);
        });

        Assert.That(new _20261016110032_0_9_0_canary_20261016_27().Migrate(root), Is.False);
    }

    [Test]
    public void Migrate_ExistingSelection_PreservesBehaviorAndPlatformRules()
    {
        var root = JsonNode.Parse("""
            {
              "ChatWindow": {
                "AutomaticallyAddTextSelection": true,
                "IsTextSelectionFullscreenExcluded": false
              },
              "TextSelection": {
                "IsEnabled": false,
                "IsFullscreenExcluded": true,
                "Windows": { "ExcludedApplications": ["existing.exe"] },
                "Mac": { "ExcludedApplications": ["com.example.existing"] }
              }
            }
            """)?.AsObject() ?? throw new InvalidOperationException();

        Assert.That(new _20261016110032_0_9_0_canary_20261016_27().Migrate(root), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(root["TextSelection"]?["IsEnabled"]?.GetValue<bool>(), Is.False);
            Assert.That(root["TextSelection"]?["IsFullscreenExcluded"]?.GetValue<bool>(), Is.True);
            Assert.That(root["TextSelection"]?["Windows"]?["ExcludedApplications"]?[0]?.GetValue<string>(), Is.EqualTo("existing.exe"));
            Assert.That(root["TextSelection"]?["Mac"]?["ExcludedApplications"]?[0]?.GetValue<string>(), Is.EqualTo("com.example.existing"));
            Assert.That(root["ChatWindow"]?.AsObject(), Is.Empty);
        });
    }
}
