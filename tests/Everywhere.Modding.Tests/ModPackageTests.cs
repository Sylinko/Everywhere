using System.IO.Compression;
using System.Text;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Exercises installation publication, cache validation, and platform selection through public production APIs.
/// </summary>
public sealed class ModPackageTests
{
    private string _root = "";

    /// <summary>
    /// Allocates a private package store for each test.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "Everywhere.Modding.Tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Removes only the test-owned store.
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A development rebuild must not mutate an already installed package with the same version.
    /// </summary>
    [Test]
    public async Task ImportAsync_SameVersionDifferentBytes_PreservesBothInstallations()
    {
        var store = new ModPackageStore(_root, Path.Combine(_root, "user-data"), Path.Combine(_root, "staging"));
        using var firstPackage = CreatePackage("first");
        using var secondPackage = CreatePackage("second");
        var first = await store.ImportAsync(firstPackage);
        var second = await store.ImportAsync(secondPackage);
        var dataDirectory = store.GetDataDirectory(first);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "saved");

        Assert.Multiple(() =>
        {
            Assert.That(first.Directory, Is.Not.EqualTo(second.Directory));
            Assert.That(File.ReadAllText(Path.Combine(first.Directory, "Entry.dll")), Is.EqualTo("first"));
            Assert.That(File.ReadAllText(Path.Combine(second.Directory, "Entry.dll")), Is.EqualTo("second"));
            Assert.That(store.GetDataDirectory(second), Is.EqualTo(dataDirectory));
            Assert.That(Directory.GetDirectories(Path.Combine(_root, "staging")), Is.Empty);
        });

        Assert.That(store.TryRemove(first), Is.True);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dataDirectory, "settings.json")), Is.EqualTo("saved"));
    }

    /// <summary>
    /// Rejected external paths cannot publish a partial installation.
    /// </summary>
    [TestCase("../outside.dll")]
    [TestCase("/rooted.dll")]
    [TestCase("C:/absolute.dll")]
    public async Task ImportAsync_InvalidArchivePath_PublishesNothing(string path)
    {
        var store = new ModPackageStore(_root, Path.Combine(_root, "user-data"), Path.Combine(_root, "staging"));
        using var package = CreatePackage("payload", path);

        await Assert.ThrowsAsync<ModValidationException>(async () => await store.ImportAsync(package));
        Assert.That(Directory.Exists(Path.Combine(_root, "packages")), Is.False);
        Assert.That(Directory.GetDirectories(Path.Combine(_root, "staging")), Is.Empty);
    }

    /// <summary>
    /// Portable packages reject aliases that would overwrite one another on Windows.
    /// </summary>
    [Test]
    public async Task ImportAsync_CaseCollidingEntries_RejectsPackage()
    {
        var store = new ModPackageStore(_root, Path.Combine(_root, "user-data"), Path.Combine(_root, "staging"));
        using var package = CreatePackage("payload", "entry.dll");

        await Assert.ThrowsAsync<ModValidationException>(async () => await store.ImportAsync(package));
    }

    /// <summary>
    /// Declaring any missing assembly, README, icon, or license file must fail validation before publication.
    /// </summary>
    [TestCase("entry")]
    [TestCase("readme")]
    [TestCase("icon")]
    [TestCase("license")]
    [TestCase("third-party")]
    public async Task ImportAsync_MissingDeclaredAssets_PublishesNothing(string missingScenario)
    {
        var store = new ModPackageStore(_root, Path.Combine(_root, "user-data"), Path.Combine(_root, "staging"));
        using var stream = new MemoryStream();
        await using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var readme = missingScenario == "readme" ? """{ "en": "README.md" }""" : "{}";
            var icon = missingScenario == "icon" ? "\"icon.svg\"" : "null";
            var license = missingScenario switch
            {
                "license" => """{ "spdx": "MIT", "file": "LICENSE" }""",
                "third-party" => """{ "spdx": "MIT", "thirdPartyFiles": ["THIRD_PARTY.txt"] }""",
                _ => "null"
            };

            WriteEntry(
                archive,
                ModManifestFile.FileName,
                $$"""
                  {
                    "schemaVersion": 1,
                    "id": "acme.example",
                    "version": "1.0.0",
                    "readme": {{readme}},
                    "icon": {{icon}},
                    "license": {{license}},
                    "entry": {
                      "$type": "universal",
                      "assembly": "Entry.dll",
                      "type": "Example.Entry",
                      "framework": "net11.0",
                      "modApiVersion": "[1.0,2.0)",
                      "assemblies": ["Entry.dll"]
                    }
                  }
                  """);

            if (missingScenario != "entry")
            {
                WriteEntry(archive, "Entry.dll", "payload");
            }
        }

        stream.Position = 0;
        var exception = await Assert.ThrowsAsync<ModValidationException>(async () => await store.ImportAsync(stream));
        Assert.That(exception?.Code, Is.EqualTo("MissingAsset"));
        Assert.That(Directory.Exists(Path.Combine(_root, "packages")), Is.False);
    }

    /// <summary>
    /// Unpacked directories copy isolated assets into the store, follow valid links, and reject cyclic links.
    /// </summary>
    [Test]
    public async Task ImportDirectoryAsync_UnpackedDirectoryAndLinks_CopiesFilesAndRejectsLinkCycles()
    {
        var source = Path.Combine(_root, "dev-source");
        var runtime = Path.Combine(_root, "runtime");
        var data = Path.Combine(_root, "data");
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(Path.Combine(source, "runtimes", "win-x64", "native"));
        await File.WriteAllTextAsync(Path.Combine(source, "Entry.dll"), "dev-bytes");
        await File.WriteAllTextAsync(Path.Combine(source, "runtimes", "win-x64", "native", "dep.dll"), "native-bytes");
        await File.WriteAllTextAsync(
            Path.Combine(source, ModManifestFile.FileName),
            """
            {
              "schemaVersion": 1,
              "id": "acme.dev",
              "version": "1.0.0",
              "entry": {
                "$type": "universal",
                "assembly": "Entry.dll",
                "type": "Example.Entry",
                "framework": "net11.0",
                "modApiVersion": "[1.0,2.0)",
                "assemblies": ["Entry.dll"]
              }
            }
            """);

        var store = new ModPackageStore(runtime, data, staging);
        var installation = await store.ImportDirectoryAsync(source);
        Assert.Multiple(() =>
        {
            Assert.That(installation.Directory, Does.StartWith(Path.Combine(runtime, "packages") + Path.DirectorySeparatorChar));
            Assert.That(File.ReadAllText(Path.Combine(installation.Directory, "Entry.dll")), Is.EqualTo("dev-bytes"));
            Assert.That(
                File.ReadAllText(Path.Combine(installation.Directory, "runtimes", "win-x64", "native", "dep.dll")),
                Is.EqualTo("native-bytes"));
            Assert.That(store.GetDataDirectory(installation), Is.EqualTo(Path.Combine(data, "acme.dev")));
            Assert.That(Directory.GetFileSystemEntries(staging), Is.Empty);
        });

        var linkedShared = Path.Combine(_root, "shared-assets");
        Directory.CreateDirectory(linkedShared);
        await File.WriteAllTextAsync(Path.Combine(linkedShared, "extra.txt"), "linked-payload");
        var linkDirectory = Path.Combine(source, "linked");

        try
        {
            Directory.CreateSymbolicLink(linkDirectory, linkedShared);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var linkedInstallation = await store.ImportDirectoryAsync(source);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(linkedInstallation.Directory, "linked", "extra.txt")), Is.EqualTo("linked-payload"));

        var cycleLink = Path.Combine(linkedShared, "loop");
        Directory.CreateSymbolicLink(cycleLink, source);
        var cycleException = await Assert.ThrowsAsync<ModValidationException>(async () => await store.ImportDirectoryAsync(source));
        Assert.That(cycleException?.Code, Is.EqualTo("SymbolicLinkCycle"));
    }

    /// <summary>
    /// Cached store installations reopen cleanly only when owned by the store and all declared files are intact.
    /// </summary>
    [Test]
    public async Task TryOpen_CachedOrCorruptedInstallation_ReopensValidAndRejectsInvalid()
    {
        var store = new ModPackageStore(_root, Path.Combine(_root, "data"), Path.Combine(_root, "staging"));
        using var package = CreatePackage("payload", version: "1.2.3-preview.1");
        var installation = await store.ImportAsync(package);

        if (!store.TryOpen(installation.Directory, out var reopened))
        {
            throw new AssertionException("Expected cached installation to reopen.");
        }

        Assert.That(reopened.InstallationId, Is.EqualTo(installation.InstallationId));
        Assert.That(reopened.Manifest.Id, Is.EqualTo("acme.example"));
        Assert.That(reopened.Manifest.Version, Is.EqualTo("1.2.3-preview.1"));

        Assert.That(store.TryOpen(_root, out _), Is.False);
        Assert.That(store.TryOpen(Path.Combine(_root, "packages", "missing"), out _), Is.False);

        File.Delete(Path.Combine(installation.Directory, "Entry.dll"));
        Assert.That(store.TryOpen(installation.Directory, out _), Is.False);
    }

    /// <summary>
    /// Directory fingerprinting stays deterministic across repeated runs and reacts to content or relative path changes.
    /// </summary>
    [Test]
    public async Task ComputeDirectoryFingerprintAsync_ModifiedFileOrStructure_ChangesDeterministically()
    {
        var source = Path.Combine(_root, "fingerprint-src");
        Directory.CreateDirectory(source);
        var fileA = Path.Combine(source, "a.txt");
        await File.WriteAllTextAsync(fileA, "hello");

        var first = await ModPackageStore.ComputeDirectoryFingerprintAsync(source);
        var second = await ModPackageStore.ComputeDirectoryFingerprintAsync(source);
        Assert.That(first, Is.EqualTo(second));
        Assert.That(first, Has.Length.EqualTo(64));

        await File.WriteAllTextAsync(fileA, "world");
        var modifiedContent = await ModPackageStore.ComputeDirectoryFingerprintAsync(source);
        Assert.That(modifiedContent, Is.Not.EqualTo(first));

        var fileB = Path.Combine(source, "b.txt");
        File.Move(fileA, fileB);
        var renamed = await ModPackageStore.ComputeDirectoryFingerprintAsync(source);
        Assert.That(renamed, Is.Not.EqualTo(modifiedContent));
    }

    /// <summary>
    /// RID selection precedes host compatibility and is independent of declaration order.
    /// </summary>
    [Test]
    public void Select_SpecificEntryIncompatible_DoesNotFallBackToGenericEntry()
    {
        var manifest = new ModManifest
        {
            Id = "acme.example",
            Version = "1.0.0",
            Entry = new PlatformModEntry
            {
                Variants = [Variant("win", "[1.0,3.0)"), Variant("win-x64", "[2.0,3.0)")]
            }
        };

        var selected = ModPlatformSelector.Select(manifest, "win-x64") ?? throw new AssertionException("Missing Windows entry.");
        var selectedRange = selected.HostVersion ?? throw new AssertionException("The specific entry lost its host range.");

        Assert.That(selectedRange, Is.EqualTo("[2.0,3.0)"));
        Assert.That(ModCompatibility.IsSatisfied(selectedRange, "1.5.0"), Is.False);
        Assert.That(ModPlatformSelector.Select(manifest, "win-arm64")?.HostVersion, Is.EqualTo("[1.0,3.0)"));
    }

    /// <summary>
    /// SDK and runtime checks use interval precedence, not an extra prerelease admission policy.
    /// </summary>
    [Test]
    public void IsSatisfied_PrereleaseCandidate_UsesVersionPrecedence()
    {
        Assert.That(ModCompatibility.IsSatisfied("[1.0,2.0)", "1.5.0-preview.1"), Is.True);
        Assert.That(ModCompatibility.IsSatisfied("[1.0-preview.1,2.0)", "1.5.0-preview.1"), Is.True);
        Assert.That(ModCompatibility.IsSatisfied("[1.0,2.0)", "2.0.0-preview.1"), Is.True);
        Assert.That(ModCompatibility.IsSatisfied("[1.0,2.0-0)", "2.0.0-preview.1"), Is.False);
        Assert.That(ModCompatibility.IsSatisfied("[1.0-preview.2,1.0)", "1.0.0-preview.10"), Is.True);
    }

    /// <summary>
    /// Creates a ZIP containing an ordinary neutral generated manifest and a distinguishable payload.
    /// </summary>
    private static MemoryStream CreatePackage(string payload, string? extraPath = null, string version = "1.0.0")
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(
                archive,
                "mod.json",
                $$"""
                  {
                    "schemaVersion": 1,
                    "id": "acme.example",
                    "version": "{{version}}",
                    "entry": {
                      "$type": "universal",
                      "assembly": "Entry.dll",
                      "type": "Example.Entry",
                      "framework": "net11.0",
                      "modApiVersion": "[1.0,2.0)",
                      "assemblies": ["Entry.dll"]
                    }
                  }
                  """);
            WriteEntry(archive, "Entry.dll", payload);

            if (extraPath is not null)
            {
                WriteEntry(archive, extraPath, "extra");
            }
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Writes a fixture entry without relying on filesystem extraction behavior.
    /// </summary>
    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// Creates a platform entry for selection tests.
    /// </summary>
    private static ModPlatformVariant Variant(string platform, string hostVersion)
    {
        return new ModPlatformVariant
        {
            Platform = platform,
            Assembly = "Entry.dll",
            Type = "Example.Entry",
            Framework = "net11.0",
            ModApiVersion = "[1.0,2.0)",
            HostVersion = hostVersion,
            Assemblies = ["Entry.dll"]
        };
    }
}
