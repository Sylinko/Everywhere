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
    /// Generated metadata must retain both entry discriminators, variant ranges and optional dependency declarations.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void WriteAndRead_EntryKinds_PreserveDeclarations(bool isPerPlatform)
    {
        var manifest = CreateManifest(isPerPlatform);
        PluginManifestFile.Write(_root, manifest);
        var restored = PluginManifestFile.Read(_root);
        var selected = PluginPlatformSelector.Select(restored, "win-x64");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, PluginManifestFile.FileName)));

        Assert.Multiple(() =>
        {
            Assert.That(restored.Id, Is.EqualTo(manifest.Id));
            Assert.That(restored.Version, Is.EqualTo(manifest.Version));
            Assert.That(restored.Entry.GetType(), Is.EqualTo(manifest.Entry.GetType()));
            Assert.That(document.RootElement.GetProperty("entry").GetProperty("$type").GetString(),
                Is.EqualTo(isPerPlatform ? "perPlatform" : "universal"));
            Assert.That(selected.HostVersion, Is.Null);
            Assert.That(selected.Dependencies.Single().IsOptional, Is.True);
            Assert.That(selected.Dependencies.Single().Assemblies, Is.EqualTo(new[] { "Other.Plugin" }));
        });

        if (isPerPlatform)
        {
            var mac = PluginPlatformSelector.Select(restored, "osx-arm64");
            Assert.That(mac.Assembly, Is.EqualTo("mac/Entry.dll"));
            Assert.That(mac.HostVersion, Is.EqualTo("[0.9.2,0.10.0)"));
        }
    }

    /// <summary>
    /// Source generation must keep unknown-member rejection, required members and nonnullable annotations effective.
    /// </summary>
    [TestCase("rootUnknown")]
    [TestCase("missingRequired")]
    [TestCase("nullRequired")]
    [TestCase("unknownDiscriminator")]
    public void Read_InvalidJsonContract_RejectsManifest(string scenario)
    {
        PluginManifestFile.Write(_root, CreateManifest(false));
        var path = Path.Combine(_root, PluginManifestFile.FileName);
        var document = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new AssertionException("Missing fixture document.");
        var entry = document["entry"]?.AsObject() ?? throw new AssertionException("Missing fixture entry.");

        switch (scenario)
        {
            case "rootUnknown":
                document["unknown"] = true;
                break;
            case "missingRequired":
                entry.Remove("assembly");
                break;
            case "nullRequired":
                entry["assemblies"] = null;
                break;
            case "unknownDiscriminator":
                entry["$type"] = "unsupported";
                break;
        }

        File.WriteAllText(path, document.ToJsonString());
        Assert.Throws<JsonException>(() => PluginManifestFile.Read(_root));
    }

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
        Assert.That(PluginPlatformSelector.Select(restored, "win-x64").Dependencies, Is.Empty);

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
    /// Creates existing public models, including a second platform with a different host range.
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
                    },
                    new PluginPlatformVariant
                    {
                        Platform = "osx",
                        Assembly = "mac/Entry.dll",
                        Type = entry.Type,
                        Framework = entry.Framework,
                        PluginApiVersion = entry.PluginApiVersion,
                        HostVersion = "[0.9.2,0.10.0)",
                        Assemblies = ["mac/Entry.dll"]
                    }
                ]
            } : entry
        };
    }
}