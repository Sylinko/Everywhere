using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Verifies generated JSON metadata against the package format and its validation boundaries.
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
    /// Missing optional fields keep defaults, unknown future fields are skipped, and explicit null collections remain invalid.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void Read_OmittedOptionalAndFutureFields_PreservesCompatibility(bool isPerPlatform)
    {
        ModManifestFile.Write(_root, CreateManifest(isPerPlatform));
        var path = Path.Combine(_root, ModManifestFile.FileName);
        var document = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new AssertionException("Missing fixture document.");
        var entry = (isPerPlatform ? document["entry"]?["variants"]?[0] : document["entry"])?.AsObject()
            ?? throw new AssertionException("Missing fixture entry.");
        document.Remove("schemaVersion");
        document["futureManifestField"] = true;
        entry.Remove("dependencies");
        entry["futureEntryField"] = 42;
        File.WriteAllText(path, document.ToJsonString());

        var restored = ModManifestFile.Read(_root);
        var selected = ModPlatformSelector.Select(restored, "win-x64");
        Assert.Multiple(() =>
        {
            Assert.That(restored.SchemaVersion, Is.EqualTo(1));
            Assert.That(selected, Is.Not.Null);
            Assert.That(selected?.Dependencies, Is.Empty);
            Assert.That(ModPlatformSelector.Select(restored, "linux-x64"), Is.EqualTo(isPerPlatform ? null : selected));
            Assert.That(
                ModPlatformSelector.GetFallbackPlatforms("win-x64").Take(3),
                Is.EqualTo(new[] { "win-x64", "win", "any" }));
        });

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
    /// Package presentation, repository, and license metadata round-trip while unsafe or broken values are rejected.
    /// </summary>
    [Test]
    public void Read_ExtendedMetadata_RoundTripsAndRejectsInvalidValues()
    {
        var manifest = CreateManifest(false) with
        {
            Author = "Sylinko",
            Icon = "Assets/icon.svg",
            Homepage = "https://example.com/mod?tab=docs#top",
            Repository = new ModRepositoryInfo
            {
                Url = "https://github.com/Sylinko/Everywhere?ref=main#readme",
                Directory = "mods/Sample"
            },
            License = new ModLicense
            {
                Spdx = "BUSL-1.1",
                File = "LICENSE",
                IsAcceptanceRequired = true,
                ThirdPartyFiles = ["Licenses/ThirdParty.txt"]
            }
        };

        ModManifestFile.Write(_root, manifest);
        var restored = ModManifestFile.Read(_root);

        Assert.Multiple(() =>
        {
            Assert.That(restored.Author, Is.EqualTo("Sylinko"));
            Assert.That(restored.Icon, Is.EqualTo("Assets/icon.svg"));
            Assert.That(restored.Homepage, Is.EqualTo("https://example.com/mod?tab=docs#top"));
            Assert.That(restored.Repository?.Url, Is.EqualTo("https://github.com/Sylinko/Everywhere?ref=main#readme"));
            Assert.That(restored.Repository?.Directory, Is.EqualTo("mods/Sample"));
            Assert.That(restored.License?.Spdx, Is.EqualTo("BUSL-1.1"));
            Assert.That(restored.License?.File, Is.EqualTo("LICENSE"));
            Assert.That(restored.License?.IsAcceptanceRequired, Is.True);
            Assert.That(restored.License?.ThirdPartyFiles, Is.EqualTo(["Licenses/ThirdParty.txt"]));
        });

        Assert.Throws<ModValidationException>(() => ModManifestFile.Validate(manifest with { Icon = "Assets/icon.exe" }));
        Assert.Throws<ModValidationException>(() => ModManifestFile.Validate(manifest with { Homepage = "https://user:pass@example.com/" }));
        Assert.Throws<ModValidationException>(() => ModManifestFile.Validate(
            manifest with
            {
                Repository = new ModRepositoryInfo { Url = "ftp://example.com/repo" }
            }));
        Assert.Throws<ModValidationException>(() => ModManifestFile.Validate(
            manifest with
            {
                License = new ModLicense { Spdx = "   " }
            }));
        Assert.Throws<ModValidationException>(() => ModManifestFile.Validate(
            manifest with
            {
                License = new ModLicense { Spdx = "MIT", File = "LICENSE", ThirdPartyFiles = ["license"] }
            }));
    }

    /// <summary>
    /// Creates each entry kind with an explicit dependency used across compatibility and null-handling tests.
    /// </summary>
    private static ModManifest CreateManifest(bool isPerPlatform)
    {
        var entry = new UniversalModEntry
        {
            Assembly = "Entry.dll",
            Type = "Example.Entry",
            Framework = "net11.0",
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
            Entry = isPerPlatform
                ? new PlatformModEntry
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
                }
                : entry
        };
    }
}
