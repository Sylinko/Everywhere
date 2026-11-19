using System.IO.Compression;
using System.Text;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Management;
using Everywhere.Modding.Hosting.Resolution;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Exercises atomic state persistence, N=1 rollback, RestartRequired cold-start reconciliation,
/// dev directory fingerprint synchronization, and one-time default enable / license acceptance.
/// </summary>
public sealed class ModStateStoreTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "Everywhere.Modding.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Updates preserve exactly one N=1 rollback slot while deleting obsolete versions cleanly.
    /// </summary>
    [Test]
    public async Task RecordInstallUpdateAndRollback_PreservesSingleRollbackSlotAndCleansOlderCopies()
    {
        var packageStore = CreatePackageStore();
        var statePath = Path.Combine(_root, ModStateStore.DefaultFileName);
        var store = new ModStateStore(statePath, packageStore);
        store.Load();

        var v1 = await ImportZipAsync(packageStore, "acme.demo", "1.0.0", "payload-v1");
        store.RecordInstallOrUpdate("acme.demo", v1, "official", ModInstallIntent.Explicit);

        var v2 = await ImportZipAsync(packageStore, "acme.demo", "1.1.0", "payload-v2");
        store.RecordInstallOrUpdate("acme.demo", v2, "official", ModInstallIntent.Explicit);

        var v3 = await ImportZipAsync(packageStore, "acme.demo", "1.2.0", "payload-v3");
        store.RecordInstallOrUpdate("acme.demo", v3, "official", ModInstallIntent.Explicit);

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(v1.Directory), Is.False, "N > 1 prior installation should be deleted.");
            Assert.That(Directory.Exists(v2.Directory), Is.True, "N = 1 previous installation must remain for rollback.");
            Assert.That(Directory.Exists(v3.Directory), Is.True);
        });

        // Reload from disk and perform N=1 rollback to v2.
        var reloaded = new ModStateStore(statePath, packageStore);
        reloaded.Load();
        Assert.That(reloaded.TryRollback("acme.demo"), Is.True);
        Assert.That(reloaded.TryOpenActiveInstallation("acme.demo", out var active), Is.True);
        Assert.That(active?.Manifest.Version, Is.EqualTo("1.1.0"));
        Assert.That(Directory.Exists(v3.Directory), Is.False);
        Assert.That(reloaded.State.Mods["acme.demo"].PreviousInstallation, Is.Null);
    }

    /// <summary>
    /// Pending updates and uninstalls queued during RestartRequired complete cleanly at next startup.
    /// </summary>
    [Test]
    public async Task ReconcileStartupAsync_PendingUpdateAndUninstallFromRestartRequired_CompletesOnColdStart()
    {
        var packageStore = CreatePackageStore();
        var statePath = Path.Combine(_root, ModStateStore.DefaultFileName);
        var store = new ModStateStore(statePath, packageStore);
        store.Load();

        var a1 = await ImportZipAsync(packageStore, "acme.a", "1.0.0", "a1");
        var b1 = await ImportZipAsync(packageStore, "acme.b", "1.0.0", "b1");
        store.RecordInstallOrUpdate("acme.a", a1, "official", ModInstallIntent.Explicit);
        store.RecordInstallOrUpdate("acme.b", b1, "official", ModInstallIntent.Explicit);

        var bDataDir = packageStore.GetDataDirectory(b1);
        await File.WriteAllTextAsync(Path.Combine(bDataDir, "prefs.json"), "{}");

        // Hot retirement returned RestartRequired -> queue update for acme.a and uninstall (+data) for acme.b.
        var a2 = await ImportZipAsync(packageStore, "acme.a", "2.0.0", "a2");
        store.RecordInstallOrUpdate("acme.a", a2, "official", ModInstallIntent.Explicit, isRestartRequired: true);
        store.RecordUninstall("acme.b", isRestartRequired: true, shouldDeleteData: true);

        Assert.That(store.TryOpenActiveInstallation("acme.a", out var stillOldA), Is.True);
        Assert.That(stillOldA?.Manifest.Version, Is.EqualTo("1.0.0"));
        Assert.That(store.State.Mods.ContainsKey("acme.b"), Is.True);

        // Cold startup reconciles pending operations before activating mods.
        var nextRun = new ModStateStore(statePath, packageStore);
        nextRun.Load();
        var diagnostics = await nextRun.ReconcileStartupAsync();
        Assert.That(diagnostics, Is.Empty);

        Assert.That(nextRun.TryOpenActiveInstallation("acme.a", out var updatedA), Is.True);
        Assert.That(updatedA?.Manifest.Version, Is.EqualTo("2.0.0"));
        Assert.That(nextRun.State.Mods["acme.a"].PreviousInstallation?.Version, Is.EqualTo("1.0.0"));
        Assert.That(Directory.Exists(a1.Directory), Is.True);

        Assert.That(nextRun.State.Mods.ContainsKey("acme.b"), Is.False);
        Assert.That(Directory.Exists(b1.Directory), Is.False);
        Assert.That(Directory.Exists(bDataDir), Is.False);
    }

    /// <summary>
    /// Unpacked development folders skip re-import when fingerprints match and re-import automatically when modified.
    /// </summary>
    [Test]
    public async Task ReconcileStartupAsync_UnpackedDevDirectory_ReimportsOnlyWhenFingerprintChanges()
    {
        var devDir = Path.Combine(_root, "dev-mod");
        Directory.CreateDirectory(devDir);
        await File.WriteAllTextAsync(Path.Combine(devDir, "Entry.dll"), "build-1");
        await File.WriteAllTextAsync(
            Path.Combine(devDir, ModManifestFile.FileName),
            """
            {
              "schemaVersion": 1,
              "id": "acme.dev",
              "version": "1.0.0",
              "entry": {
                "$type": "universal",
                "assembly": "Entry.dll",
                "type": "Dev.Entry",
                "framework": "net11.0",
                "modApiVersion": "[1.0,2.0)",
                "assemblies": ["Entry.dll"]
              }
            }
            """);

        var packageStore = CreatePackageStore();
        var statePath = Path.Combine(_root, ModStateStore.DefaultFileName);
        var store = new ModStateStore(statePath, packageStore);
        store.Load();

        var initialInstall = await packageStore.ImportDirectoryAsync(devDir);
        var fp1 = await ModPackageStore.ComputeDirectoryFingerprintAsync(devDir);
        store.RecordInstallOrUpdate(
            "acme.dev",
            initialInstall,
            ModStateRecord.LocalDirectorySourceId,
            ModInstallIntent.Explicit,
            localDirectoryPath: devDir,
            localDirectoryFingerprint: fp1);

        var unchangedDiagnostics = await store.ReconcileStartupAsync();
        Assert.That(unchangedDiagnostics, Is.Empty);
        Assert.That(store.State.Mods["acme.dev"].ActiveInstallation?.InstallationId, Is.EqualTo(initialInstall.InstallationId));

        await File.WriteAllTextAsync(Path.Combine(devDir, "Entry.dll"), "build-2");
        var updatedDiagnostics = await store.ReconcileStartupAsync();
        Assert.That(updatedDiagnostics, Is.Empty);
        if (!store.TryOpenActiveInstallation("acme.dev", out var updatedInstall))
        {
            throw new AssertionException("Expected updated dev installation to reopen.");
        }

        Assert.That(updatedInstall.InstallationId, Is.Not.EqualTo(initialInstall.InstallationId));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(updatedInstall.Directory, "Entry.dll")), Is.EqualTo("build-2"));
        Assert.That(Directory.Exists(initialInstall.Directory), Is.False);
    }

    /// <summary>
    /// Verifies one-time IsDefaultEnabled claiming and EULA acceptance invalidation when SPDX or text hash changes.
    /// </summary>
    [Test]
    public void ClaimDefaultEnabledMods_AndLicenseAcceptance_EnablesOnceAndTracksLicenseChanges()
    {
        var packageStore = CreatePackageStore();
        var statePath = Path.Combine(_root, ModStateStore.DefaultFileName);
        var store = new ModStateStore(statePath, packageStore);
        store.Load();

        var index = new ModIndex
        {
            Id = "native",
            Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Native" },
            Mods =
            [
                new ModIndexEntry
                {
                    Id = "mod.default",
                    IsDefaultEnabled = true,
                    Versions = [DummyVersion("1.0.0")]
                },
                new ModIndexEntry
                {
                    Id = "mod.optin",
                    IsDefaultEnabled = false,
                    Versions = [DummyVersion("1.0.0")]
                }
            ]
        };

        var claimed = store.ClaimDefaultEnabledMods(index);
        Assert.That(claimed, Is.EqualTo(new[] { "mod.default" }));

        var reloaded = new ModStateStore(statePath, packageStore);
        reloaded.Load();
        Assert.That(reloaded.ClaimDefaultEnabledMods(index), Is.Empty);

        reloaded.State.Mods["mod.eula"] = new ModStateRecord { Id = "mod.eula", SourceId = "official" };
        Assert.That(ModStateStore.RequiresLicenseAcceptance(reloaded.State.Mods["mod.eula"], "BUSL-1.1", true, "hash1"), Is.True);

        reloaded.RecordLicenseAcceptance("mod.eula", "BUSL-1.1", "hash1", acceptedAtUnixSeconds: 100);
        Assert.That(ModStateStore.RequiresLicenseAcceptance(reloaded.State.Mods["mod.eula"], "BUSL-1.1", true, "hash1"), Is.False);
        Assert.That(ModStateStore.RequiresLicenseAcceptance(reloaded.State.Mods["mod.eula"], "BUSL-1.1", true, "hash2"), Is.True);
        Assert.That(ModStateStore.RequiresLicenseAcceptance(reloaded.State.Mods["mod.eula"], "MIT", true, "hash1"), Is.True);
        Assert.That(ModStateStore.RequiresLicenseAcceptance(reloaded.State.Mods["mod.eula"], "MIT", false, "hash1"), Is.False);
    }

    private ModPackageStore CreatePackageStore() =>
        new(Path.Combine(_root, "runtime"), Path.Combine(_root, "data"), Path.Combine(_root, "staging"));

    private static async Task<ModInstallation> ImportZipAsync(ModPackageStore store, string modId, string version, string payload)
    {
        using var stream = new MemoryStream();
        await using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var manifest = archive.CreateEntry(ModManifestFile.FileName).Open())
            {
                await manifest.WriteAsync(
                    Encoding.UTF8.GetBytes(
                        $$"""
                          {
                            "schemaVersion": 1,
                            "id": "{{modId}}",
                            "version": "{{version}}",
                            "entry": {
                              "$type": "universal",
                              "assembly": "Entry.dll",
                              "type": "Sample.Entry",
                              "framework": "net11.0",
                              "modApiVersion": "[1.0,2.0)",
                              "assemblies": ["Entry.dll"]
                            }
                          }
                          """));
            }

            await using (var dll = archive.CreateEntry("Entry.dll").Open())
            {
                await dll.WriteAsync(Encoding.UTF8.GetBytes(payload));
            }
        }

        stream.Position = 0;
        return await store.ImportAsync(stream);
    }

    private static ModIndexVersion DummyVersion(string version) =>
        new()
        {
            Version = version,
            Entry = new UniversalModEntry
            {
                Assembly = "Entry.dll",
                Type = "Sample.Entry",
                Framework = "net11.0",
                ModApiVersion = "[1.0,2.0)",
                Assemblies = ["Entry.dll"]
            },
            Artifact = new ModArtifact { Url = "mod.zip", Size = 128, Sha256 = new string('a', 64) }
        };
}
