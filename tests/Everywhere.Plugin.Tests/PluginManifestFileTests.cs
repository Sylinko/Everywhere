using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Tests;

/// <summary>
/// Verifies generated JSON metadata against the package format and its existing validation boundary.
/// </summary>
public sealed class PluginManifestFileTests
{
    private string _root = "";

    /// <summary>
    /// Creates an isolated manifest directory.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "Everywhere.PluginManifest.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Removes the test-owned manifest directory.
    /// </summary>
    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Missing fields keep their model defaults; explicit null dependencies and unsupported schema values remain invalid.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void Read_OmittedOptionalFields_PreservesDefaultsAndValidation(bool isPerPlatform)
    {
        PluginManifestFile.Write(_root, CreateManifest(isPerPlatform));
        var path = Path.Combine(_root, PluginManifestFile.FileName);
        var document = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new AssertionException("Missing fixture document.");
        var entry = (isPerPlatform ? document["entry"]?["variants"]?[0] : document["entry"])?.AsObject()
            ?? throw new AssertionException("Missing fixture entry.");
        document.Remove("schemaVersion");
        entry.Remove("dependencies");
        File.WriteAllText(path, document.ToJsonString());
        var restored = PluginManifestFile.Read(_root);
        Assert.That(restored.SchemaVersion, Is.EqualTo(1));
        Assert.That(PluginPlatformSelector.Select(restored, "win-x64")?.Dependencies, Is.Empty);

        entry["dependencies"] = null;
        File.WriteAllText(path, document.ToJsonString());
        Assert.Throws<JsonException>(() => PluginManifestFile.Read(_root));

        entry.Remove("dependencies");
        document["schemaVersion"] = 0;
        File.WriteAllText(path, document.ToJsonString());
        var exception = Assert.Throws<PluginValidationException>(() => PluginManifestFile.Read(_root));
        Assert.That(exception?.Code, Is.EqualTo("InvalidManifest"));
    }

    /// <summary>
    /// Creates each entry kind with an explicit dependency that the test removes or replaces with null.
    /// </summary>
    private static PluginManifest CreateManifest(bool isPerPlatform)
    {
        var entry = new UniversalPluginEntry
        {
            Assembly = "Entry.dll",
            Type = "Example.Entry",
            Framework = "net10.0",
            PluginApiVersion = "[1.0,2.0)",
            Assemblies = ["Entry.dll"],
            Dependencies =
            [
                new PluginDependency
                {
                    Id = "other.plugin",
                    Version = "[1.0,2.0)",
                    IsOptional = true,
                    Assemblies = ["Other.Plugin"]
                }
            ]
        };

        return new PluginManifest
        {
            Id = "acme.example",
            Version = "1.0.0",
            Entry = isPerPlatform ? new PlatformPluginEntry
            {
                Variants =
                [
                    new PluginPlatformVariant
                    {
                        Platform = "win",
                        Assembly = entry.Assembly,
                        Type = entry.Type,
                        Framework = entry.Framework,
                        PluginApiVersion = entry.PluginApiVersion,
                        Assemblies = entry.Assemblies,
                        Dependencies = entry.Dependencies
                    }
                ]
            } : entry
        };
    }
}
