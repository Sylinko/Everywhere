using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Verifies generated JSON metadata against the package format and its existing validation boundary.
/// </summary>
public sealed class ModManifestFileTests
{
    private string _root = "";

    /// <summary>
    /// Creates an isolated manifest directory.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "Everywhere.ModManifest.Tests", Guid.NewGuid().ToString("N"));
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
        ModManifestFile.Write(_root, CreateManifest(isPerPlatform));
        var path = Path.Combine(_root, ModManifestFile.FileName);
        var document = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new AssertionException("Missing fixture document.");
        var entry = (isPerPlatform ? document["entry"]?["variants"]?[0] : document["entry"])?.AsObject()
            ?? throw new AssertionException("Missing fixture entry.");
        document.Remove("schemaVersion");
        entry.Remove("dependencies");
        File.WriteAllText(path, document.ToJsonString());
        var restored = ModManifestFile.Read(_root);
        Assert.That(restored.SchemaVersion, Is.EqualTo(1));
        Assert.That(ModPlatformSelector.Select(restored, "win-x64")?.Dependencies, Is.Empty);

        entry["dependencies"] = null;
        File.WriteAllText(path, document.ToJsonString());
        Assert.Throws<JsonException>(() => ModManifestFile.Read(_root));

        entry.Remove("dependencies");
        document["schemaVersion"] = 0;
        File.WriteAllText(path, document.ToJsonString());
        var exception = Assert.Throws<ModValidationException>(() => ModManifestFile.Read(_root));
        Assert.That(exception?.Code, Is.EqualTo("InvalidManifest"));
    }

    /// <summary>
    /// Creates each entry kind with an explicit dependency that the test removes or replaces with null.
    /// </summary>
    private static ModManifest CreateManifest(bool isPerPlatform)
    {
        var entry = new UniversalModEntry
        {
            Assembly = "Entry.dll",
            Type = "Example.Entry",
            Framework = "net10.0",
            ModApiVersion = "[1.0,2.0)",
            Assemblies = ["Entry.dll"],
            Dependencies =
            [
                new ModDependency
                {
                    Id = "other.mod",
                    Version = "[1.0,2.0)",
                    IsOptional = true,
                    Assemblies = ["Other.Mod"]
                }
            ]
        };

        return new ModManifest
        {
            Id = "acme.example",
            Version = "1.0.0",
            Entry = isPerPlatform ? new PlatformModEntry
            {
                Variants =
                [
                    new ModPlatformVariant
                    {
                        Platform = "win",
                        Assembly = entry.Assembly,
                        Type = entry.Type,
                        Framework = entry.Framework,
                        ModApiVersion = entry.ModApiVersion,
                        Assemblies = entry.Assemblies,
                        Dependencies = entry.Dependencies
                    }
                ]
            } : entry
        };
    }
}
