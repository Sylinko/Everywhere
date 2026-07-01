using System.Text.Json.Nodes;
using Everywhere.Configuration;
using Everywhere.Configuration.Migrations;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Everywhere.Core.Tests.Configuration;

public sealed class ShortcutSettingsMigrationTests
{
    [Test]
    public void Migrate_0_8_0_MovesLegacyShortcutValuesToMain()
    {
        using var file = TestSettingsFile("""
        {
          "Version": "0.7.0",
          "Shortcut": {
            "ChatWindow": {
              "Key": "K",
              "Modifiers": "Control, Shift"
            },
            "PickVisualElement": {
              "Key": "P",
              "Modifiers": "Alt"
            },
            "TakeScreenshot": {
              "Key": "S",
              "Modifiers": "Meta, Shift"
            }
          }
        }
        """);

        Migrate(file.Path);

        var shortcut = ReadShortcut(file.Path);

        AssertShortcut(shortcut, "ChatWindow", "K", "Control, Shift");
        AssertShortcut(shortcut, "PickVisualElement", "P", "Alt");
        AssertShortcut(shortcut, "TakeScreenshot", "S", "Meta, Shift");
    }

    [Test]
    public void Migrate_0_8_0_FillsMissingMainValuesWithoutOverwritingExistingValues()
    {
        using var file = TestSettingsFile("""
        {
          "Version": "0.7.0",
          "Shortcut": {
            "ChatWindow": {
              "Main": {
                "Key": "Existing"
              },
              "Key": "Legacy",
              "Modifiers": "Control"
            },
            "PickVisualElement": {
              "Main": {
                "Key": "ExistingKey",
                "Modifiers": "ExistingModifiers"
              },
              "Key": "LegacyKey",
              "Modifiers": "LegacyModifiers"
            }
          }
        }
        """);

        Migrate(file.Path);

        var shortcut = ReadShortcut(file.Path);

        AssertShortcut(shortcut, "ChatWindow", "Existing", "Control");
        AssertShortcut(shortcut, "PickVisualElement", "ExistingKey", "ExistingModifiers");
    }

    private static void Migrate(string path)
    {
        var migrator = new SettingsMigrator(
            path,
            [new _20260614154350_0_8_0()],
            Substitute.For<ILogger>());

        migrator.Migrate();
    }

    private static JsonObject ReadShortcut(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.That(root["Version"]!.GetValue<string>(), Is.EqualTo("0.8.0"));
        return root["Shortcut"]!.AsObject();
    }

    private static void AssertShortcut(JsonObject shortcut, string name, string key, string modifiers)
    {
        var shortcutObject = shortcut[name]!.AsObject();
        var main = shortcutObject["Main"]!.AsObject();

        Assert.That(main["Key"]!.GetValue<string>(), Is.EqualTo(key));
        Assert.That(main["Modifiers"]!.GetValue<string>(), Is.EqualTo(modifiers));
        Assert.That(shortcutObject.ContainsKey("Key"), Is.False);
        Assert.That(shortcutObject.ContainsKey("Modifiers"), Is.False);
    }

    private static TempSettingsFile TestSettingsFile(string json) => new(json);

    private sealed class TempSettingsFile : IDisposable
    {
        private readonly string _directory;

        public TempSettingsFile(string json)
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Everywhere.Core.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "settings.json");
            File.WriteAllText(Path, json);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }
}
