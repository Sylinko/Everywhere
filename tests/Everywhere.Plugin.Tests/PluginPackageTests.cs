using System.IO.Compression;
using System.Text;
using Everywhere.Plugin.Hosting.Installation;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Tests;

/// <summary>
/// Exercises installation publication and platform selection through public production APIs.
/// </summary>
public sealed class PluginPackageTests
{
    private string _root = "";

    /// <summary>
    /// Allocates a private package store for each test.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "Everywhere.Plugin.Tests", Guid.NewGuid().ToString("N"));
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
        var store = new PluginPackageStore(_root, Path.Combine(_root, "user-data"), Path.Combine(_root, "staging"));
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
        Assert.That(File.ReadAllText(Path.Combine(dataDirectory, "settings.json")), Is.EqualTo("saved"));
    }

    /// <summary>
    /// Rejected external paths cannot publish a partial installation.
    /// </summary>
    [TestCase("../outside.dll")]
    [TestCase("/rooted.dll")]
    [TestCase("C:/absolute.dll")]
    public void ImportAsync_InvalidArchivePath_PublishesNothing(string path)
    {
        var store = new PluginPackageStore(_root, Path.Combine(_root, "user-data"), Path.Combine(_root, "staging"));
        using var package = CreatePackage("payload", path);

        Assert.ThrowsAsync<PluginValidationException>(async () => await store.ImportAsync(package));
        Assert.That(Directory.Exists(Path.Combine(_root, "packages")), Is.False);
        Assert.That(Directory.GetDirectories(Path.Combine(_root, "staging")), Is.Empty);
    }

    /// <summary>
    /// Original ZIP/folder membership controls startup selection while unchanged runtime copies are reused.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ImportSources_ZipOrFolder_ReusesCopiesAndKeepsUserData(bool isDirectory)
    {
        var sources = Path.Combine(_root, "profile", "plugins");
        var data = Path.Combine(_root, "profile", "plugin-data");
        var runtime = Path.Combine(_root, "app-data", "plugin-installations");
        var staging = Path.Combine(_root, "app-data", "cache", "plugin-staging");
        Directory.CreateDirectory(sources);
        var source = Path.Combine(sources, isDirectory ? "example" : "example.zip");
        using var package = CreatePackage("first");

        if (isDirectory)
        {
            ZipFile.ExtractToDirectory(package, source);
        }
        else
        {
            await File.WriteAllBytesAsync(source, package.ToArray());
        }

        var store = new PluginPackageStore(runtime, data, staging);
        var catalog = new PluginSourceCatalog(sources, store);
        var first = (await catalog.ImportAsync()).Single();
        var persistent = store.GetDataDirectory(first);
        await File.WriteAllTextAsync(Path.Combine(persistent, "settings.json"), "saved");
        var repeated = (await new PluginSourceCatalog(sources, store).ImportAsync()).Single();
        Assert.That(repeated.Directory, Is.EqualTo(first.Directory));
        Assert.That(first.Directory, Does.StartWith(runtime + Path.DirectorySeparatorChar));
        Assert.That(persistent, Is.EqualTo(Path.Combine(data, "acme.example")));
        Assert.That(Directory.GetFileSystemEntries(staging), Is.Empty);

        if (isDirectory)
        {
            await File.WriteAllTextAsync(Path.Combine(source, "Entry.dll"), "second");
        }
        else
        {
            using var replacement = CreatePackage("second");
            await File.WriteAllBytesAsync(source, replacement.ToArray());
        }

        var updated = (await catalog.ImportAsync()).Single();
        Assert.That(updated.Directory, Is.Not.EqualTo(first.Directory));
        Assert.That(File.ReadAllText(Path.Combine(first.Directory, "Entry.dll")), Is.EqualTo("first"));
        Assert.That(File.ReadAllText(Path.Combine(updated.Directory, "Entry.dll")), Is.EqualTo("second"));
        Assert.That(store.GetDataDirectory(updated), Is.EqualTo(persistent));

        if (isDirectory) Directory.Move(source, Path.Combine(_root, "removed-source"));
        else File.Move(source, Path.Combine(_root, "removed.zip"));

        Assert.That(await catalog.ImportAsync(), Is.Empty);
        Assert.That(File.ReadAllText(Path.Combine(persistent, "settings.json")), Is.EqualTo("saved"));
    }

    /// <summary>
    /// A ZIP and folder for the same plugin must not select a winner by enumeration order.
    /// </summary>
    [Test]
    public async Task ImportSources_DuplicateId_RejectsWithoutPublishingSelection()
    {
        var sources = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sources);
        using var package = CreatePackage("payload");
        await File.WriteAllBytesAsync(Path.Combine(sources, "one.zip"), package.ToArray());
        ZipFile.ExtractToDirectory(package, Path.Combine(sources, "two"));
        var store = new PluginPackageStore(Path.Combine(_root, "runtime"), Path.Combine(_root, "data"), Path.Combine(_root, "cache"));

        var exception = Assert.ThrowsAsync<PluginValidationException>(async () => await new PluginSourceCatalog(sources, store).ImportAsync());
        Assert.That(exception?.Code, Is.EqualTo("DuplicatePlugin"));
        Assert.That(File.Exists(Path.Combine(store.RootDirectory, "sources.json")), Is.False);
        Assert.That(File.Exists(Path.Combine(sources, "one.zip")), Is.True);
    }

    /// <summary>
    /// Portable packages reject aliases that would overwrite one another on Windows.
    /// </summary>
    [Test]
    public void ImportAsync_CaseCollidingEntries_RejectsPackage()
    {
        var store = new PluginPackageStore(_root, Path.Combine(_root, "user-data"), Path.Combine(_root, "staging"));
        using var package = CreatePackage("payload", "entry.dll");

        Assert.ThrowsAsync<PluginValidationException>(async () => await store.ImportAsync(package));
    }

    /// <summary>
    /// RID selection precedes host compatibility and is independent of declaration order.
    /// </summary>
    [Test]
    public void Select_SpecificEntryIncompatible_DoesNotFallBackToGenericEntry()
    {
        var manifest = new PluginManifest
        {
            Id = "acme.example",
            Version = "1.0.0",
            Entry = new PlatformPluginEntry
            {
                Variants = [Variant("win", "[1.0,3.0)"), Variant("win-x64", "[2.0,3.0)")]
            }
        };

        var selected = PluginPlatformSelector.Select(manifest, "win-x64");
        var selectedRange = selected.HostVersion ?? throw new AssertionException("The specific entry lost its host range.");

        Assert.That(selectedRange, Is.EqualTo("[2.0,3.0)"));
        Assert.That(PluginCompatibility.IsSatisfied(selectedRange, "1.5.0"), Is.False);
        Assert.That(PluginPlatformSelector.Select(manifest, "win-arm64").HostVersion, Is.EqualTo("[1.0,3.0)"));
    }

    /// <summary>
    /// A prerelease is not silently admitted by an ordinary stable compatibility declaration.
    /// </summary>
    [Test]
    public void IsSatisfied_PrereleaseCandidate_RequiresExplicitPrereleaseBound()
    {
        Assert.That(PluginCompatibility.IsSatisfied("[1.0,2.0)", "1.5.0-preview.1"), Is.False);
        Assert.That(PluginCompatibility.IsSatisfied("[1.0-preview.1,2.0)", "1.5.0-preview.1"), Is.True);
    }

    /// <summary>
    /// Creates a ZIP containing an ordinary neutral generated manifest and a distinguishable payload.
    /// </summary>
    private static MemoryStream CreatePackage(string payload, string? extraPath = null)
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(
                archive,
                "plugin.json",
                """
                {
                  "schemaVersion": 1,
                  "id": "acme.example",
                  "version": "1.0.0",
                  "entry": {
                    "$type": "universal",
                    "assembly": "Entry.dll",
                    "type": "Example.Entry",
                    "framework": "net10.0",
                    "pluginApiVersion": "[1.0,2.0)",
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
    private static PluginPlatformVariant Variant(string platform, string hostVersion)
    {
        return new PluginPlatformVariant
        {
            Platform = platform,
            Assembly = "Entry.dll",
            Type = "Example.Entry",
            Framework = "net10.0",
            PluginApiVersion = "[1.0,2.0)",
            HostVersion = hostVersion,
            Assemblies = ["Entry.dll"]
        };
    }
}