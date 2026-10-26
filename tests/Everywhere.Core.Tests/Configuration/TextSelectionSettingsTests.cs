using System.Text.Json.Nodes;
using Everywhere.Configuration;
using Everywhere.Configuration.Migrations;
using NSubstitute;

namespace Everywhere.Core.Tests.Configuration;

public sealed class TextSelectionSettingsTests
{
#if WINDOWS
    [TestCase("Code.exe", @"C:\Apps\CODE.EXE", true)]
    [TestCase(@"C:\Apps\Code.exe", @"c:\apps\code.exe", true)]
    [TestCase(@"C:\Apps\Code.exe", @"D:\Apps\Code.exe", false)]
    [TestCase("*.exe", @"C:\Apps\Code.exe", false)]
#elif MACOS
    [TestCase("com.example.Editor", "com.example.Editor", true)]
    [TestCase("com.example.Editor", "com.example.editor", false)]
    [TestCase("com.example.*", "com.example.Editor", false)]
#elif LINUX
    [TestCase("/usr/bin/editor", "/usr/bin/editor", true)]
    [TestCase("/usr/bin/editor", "/usr/bin/Editor", false)]
    [TestCase("/usr/bin/*", "/usr/bin/editor", false)]
#else
#error Implement application identity matching tests for this platform.
#endif
    public void ToolbarExclusion_WhenApplicationMatches_DoesNotChangeCaptureRules(string rule, string identity, bool isExpectedMatch)
    {
        var settings = new Settings(Substitute.For<IServiceProvider>()).TextSelection;
        settings.Platform.ExcludedApplications = ["globally-excluded"];
        settings.Platform.ToolbarExcludedApplications = [rule];

        Assert.Multiple(() =>
        {
            Assert.That(settings.Platform.IsToolbarExcluded(identity), Is.EqualTo(isExpectedMatch));
            Assert.That(settings.CreateMonitoringConfiguration().ExcludedApplications, Is.EqualTo(new[] { "globally-excluded" }));
        });
    }

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

        Assert.That(new _20261016110032_0_9_0_canary_20261022_27().Migrate(root), Is.True);
        var selection = root["TextSelection"]?.AsObject() ?? throw new InvalidOperationException();
        Assert.Multiple(() =>
        {
            Assert.That(selection["IsAutomaticAttachmentEnabled"]?.GetValue<bool>(), Is.True);
            Assert.That(selection["IsFullscreenExcluded"]?.GetValue<bool>(), Is.False);
            Assert.That(selection.ContainsKey("Windows"), Is.False);
            Assert.That(selection.ContainsKey("Mac"), Is.False);
            Assert.That(root["ChatWindow"]?["AutomaticallyAddTextSelection"], Is.Null);
            Assert.That(root["ChatWindow"]?["IsTextSelectionFullscreenExcluded"], Is.Null);
        });

        Assert.That(new _20261016110032_0_9_0_canary_20261022_27().Migrate(root), Is.False);
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
                "IsAutomaticAttachmentEnabled": false,
                "IsFullscreenExcluded": true,
                "Windows": { "ExcludedApplications": ["existing.exe"] },
                "Mac": { "ExcludedApplications": ["com.example.existing"] }
              }
            }
            """)?.AsObject() ?? throw new InvalidOperationException();

        Assert.That(new _20261016110032_0_9_0_canary_20261022_27().Migrate(root), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(root["TextSelection"]?["IsAutomaticAttachmentEnabled"]?.GetValue<bool>(), Is.False);
            Assert.That(root["TextSelection"]?["IsFullscreenExcluded"]?.GetValue<bool>(), Is.True);
            Assert.That(root["TextSelection"]?["Windows"]?["ExcludedApplications"]?[0]?.GetValue<string>(), Is.EqualTo("existing.exe"));
            Assert.That(root["TextSelection"]?["Mac"]?["ExcludedApplications"]?[0]?.GetValue<string>(), Is.EqualTo("com.example.existing"));
            Assert.That(root["ChatWindow"]?.AsObject(), Is.Empty);
        });
    }
}
