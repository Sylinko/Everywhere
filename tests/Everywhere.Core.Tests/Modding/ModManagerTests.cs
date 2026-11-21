using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Modding;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Hosting.Management;
using Everywhere.Modding.Hosting.Resolution;
using Everywhere.Modding.Hosting.Services;
using Everywhere.Modding.Hosting.Sources;
using Everywhere.Modding.Packaging;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Exercises <see cref="ModManager"/> lifecycle reconciliation, built-in embedded sources,
/// official source gating, local imports, rollback, and dependency confirmation guards.
/// </summary>
public sealed class ModManagerTests
{
    [Test]
    public async Task InitializeAsync_BuiltInDefaultEnabledMod_RespectsDisableAndUninstallAcrossRestarts()
    {
        await using var harness = new Harness();
        var zip100 = CreateFixtureZipBytes("acme.builtin", "1.0.0");
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["index.json"] = CreateDirectoryIndexBytes("builtin", ("acme.builtin", "1.0.0", "packages/acme.builtin-1.0.0.zip", zip100, true)),
            ["packages/acme.builtin-1.0.0.zip"] = zip100
        };
        var builtInSource = CreateEmbeddedSource("builtin", resources);

        await using (var manager1 = harness.CreateManager(builtInSources: [builtInSource]))
        {
            await manager1.InitializeAsync(CancellationToken.None);

            Assert.That(manager1.InstalledMods, Has.Count.EqualTo(1));
            var mod = manager1.InstalledMods.Single();
            Assert.That(mod.Id, Is.EqualTo("acme.builtin"));
            Assert.That(mod.Version, Is.EqualTo("1.0.0"));
            Assert.That(mod.SourceId, Is.EqualTo("builtin"));
            Assert.That(mod.IsEnabled, Is.True);
            Assert.That(mod.State, Is.EqualTo(ModState.Active));
            Assert.That(harness.Trace, Does.Contain("init:acme.builtin:1.0.0"));

            var disableResult = await manager1.SetEnabledAsync("acme.builtin", isEnabled: false);
            Assert.That(disableResult.Status, Is.EqualTo(ModOperationStatus.Applied));
            Assert.That(manager1.InstalledMods.Single().IsEnabled, Is.False);
            Assert.That(manager1.InstalledMods.Single().State, Is.EqualTo(ModState.Inactive));
        }

        harness.Trace.Clear();

        // Next launch must keep explicit user disablement intact without re-enabling or re-installing.
        await using (var manager2 = harness.CreateManager(builtInSources: [builtInSource]))
        {
            await manager2.InitializeAsync(CancellationToken.None);

            Assert.That(manager2.InstalledMods, Has.Count.EqualTo(1));
            Assert.That(manager2.InstalledMods.Single().IsEnabled, Is.False);
            Assert.That(manager2.InstalledMods.Single().State, Is.EqualTo(ModState.Inactive));
            Assert.That(harness.Trace, Is.Empty);

            var uninstallResult = await manager2.UninstallAsync("acme.builtin");
            Assert.That(uninstallResult.Status, Is.EqualTo(ModOperationStatus.Applied));
            Assert.That(manager2.InstalledMods, Is.Empty);
        }

        // Next launch must respect explicit user uninstall and not re-claim isDefaultEnabled.
        await using (var manager3 = harness.CreateManager(builtInSources: [builtInSource]))
        {
            await manager3.InitializeAsync(CancellationToken.None);

            Assert.That(manager3.InstalledMods, Is.Empty);
            Assert.That(harness.Trace, Is.Empty);
        }
    }

    [Test]
    public async Task InitializeAsync_BuiltInUpgrade_PreservesDisabledFlagAndRetainsRollback()
    {
        await using var harness = new Harness();
        var zip100 = CreateFixtureZipBytes("acme.builtin", "1.0.0");
        var zip110 = CreateFixtureZipBytes("acme.builtin", "1.1.0");
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["index.json"] = CreateDirectoryIndexBytes("builtin", ("acme.builtin", "1.0.0", "packages/acme.builtin-1.0.0.zip", zip100, true)),
            ["packages/acme.builtin-1.0.0.zip"] = zip100
        };
        var builtInSource = CreateEmbeddedSource("builtin", resources);

        await using (var manager1 = harness.CreateManager(builtInSources: [builtInSource]))
        {
            await manager1.InitializeAsync(CancellationToken.None);
            await manager1.SetEnabledAsync("acme.builtin", isEnabled: false);
        }

        // Swap embedded payload to 1.1.0 while the mod is disabled.
        resources["index.json"] = CreateDirectoryIndexBytes("builtin", ("acme.builtin", "1.1.0", "packages/acme.builtin-1.1.0.zip", zip110, true));
        resources["packages/acme.builtin-1.1.0.zip"] = zip110;
        var upgradedSource = CreateEmbeddedSource("builtin", resources);
        harness.Trace.Clear();

        await using (var manager2 = harness.CreateManager(builtInSources: [upgradedSource]))
        {
            await manager2.InitializeAsync(CancellationToken.None);

            var mod = manager2.InstalledMods.Single();
            Assert.That(mod.Version, Is.EqualTo("1.1.0"));
            Assert.That(mod.HasRollback, Is.True);
            Assert.That(mod.PreviousVersion, Is.EqualTo("1.0.0"));
            Assert.That(mod.IsEnabled, Is.False);
            Assert.That(mod.State, Is.EqualTo(ModState.Inactive));
            Assert.That(harness.Trace, Is.Empty);
        }
    }

    [Test]
    public async Task OfficialSource_VisibilityReflectsConfiguredUrlAndPublicKey()
    {
        await using var harness = new Harness();

        await using (var unconfigured = harness.CreateManager(officialUrl: string.Empty, officialPublicKey: string.Empty))
        {
            await unconfigured.InitializeAsync(CancellationToken.None);

            Assert.That(unconfigured.Sources.Any(s => s.Id == ModManager.OfficialSourceId), Is.False);
            var catalogs = await unconfigured.FetchCatalogsAsync();
            Assert.That(catalogs, Is.Empty);
        }

        var (_, publicKey) = ModSignature.GenerateKeyPair();
        await using (var configured = harness.CreateManager(
                         officialUrl: "https://mods.everywhere.test/index.json",
                         officialPublicKey: publicKey))
        {
            await configured.InitializeAsync(CancellationToken.None);

            var official = configured.Sources.Single(s => s.Id == ModManager.OfficialSourceId);
            Assert.Multiple(() =>
            {
                Assert.That(official.Url, Is.EqualTo("https://mods.everywhere.test/index.json"));
                Assert.That(official.IsBuiltIn, Is.True);
                Assert.That(official.IsSignatureRequired, Is.True);
                Assert.That(official.IsEnabled, Is.True);
                Assert.That(official.PublicKey, Is.EqualTo(publicKey));
            });

            var removeResult = await configured.RemoveCustomSourceAsync(ModManager.OfficialSourceId);
            Assert.That(removeResult.Status, Is.EqualTo(ModOperationStatus.Failed));
        }
    }

    [Test]
    public async Task LocalImportReloadRollbackAndCascadeGuards_EnforceConstraintsCleanly()
    {
        await using var harness = new Harness();
        var libDir = Path.Combine(harness.Root, "dev-lib");
        WriteFixtureDirectory(libDir, "acme.lib", "1.0.0", readmeContent: "# Lib v1");

        var appZipPath = Path.Combine(harness.Root, "acme.app-1.0.0.zip");
        await File.WriteAllBytesAsync(
            appZipPath,
            CreateFixtureZipBytes(
                "acme.app",
                "1.0.0",
                dependencies: [new ModDependency { Id = "acme.lib", Version = "[1.0.0,2.0.0)", Assemblies = [] }]));

        var lib110ZipPath = Path.Combine(harness.Root, "acme.lib-1.1.0.zip");
        await File.WriteAllBytesAsync(lib110ZipPath, CreateFixtureZipBytes("acme.lib", "1.1.0"));

        await using var manager = harness.CreateManager();
        await manager.InitializeAsync(CancellationToken.None);

        var stateChangedCount = new int[1];
        manager.StateChanged += (_, _) => Interlocked.Increment(ref stateChangedCount[0]);

        var libImport = await manager.ImportDirectoryAsync(libDir);
        Assert.That(libImport.Status, Is.EqualTo(ModOperationStatus.Applied));
        Assert.That(await manager.ReadInstalledDocumentAsync("acme.lib", "README.md"), Is.EqualTo("# Lib v1"));

        // Edit unpacked directory README and trigger hot reload.
        await File.WriteAllTextAsync(Path.Combine(libDir, "README.md"), "# Lib v1 Updated");
        var reloaded = await manager.ReloadLocalDirectoryAsync("acme.lib");
        Assert.That(reloaded.Status, Is.EqualTo(ModOperationStatus.Applied));
        Assert.That(await manager.ReadInstalledDocumentAsync("acme.lib", "README.md"), Is.EqualTo("# Lib v1 Updated"));

        var appImport = await manager.ImportZipAsync(appZipPath);
        Assert.That(appImport.Status, Is.EqualTo(ModOperationStatus.Applied));
        Assert.That(manager.GetReverseDependents("acme.lib"), Is.EqualTo(new[] { "acme.app" }));

        // Disabling shared dependency without cascade requires confirmation.
        var disableGuard = await manager.SetEnabledAsync("acme.lib", isEnabled: false);
        Assert.That(disableGuard.Status, Is.EqualTo(ModOperationStatus.ConfirmationRequired));
        Assert.That(disableGuard.AffectedDependents, Is.EqualTo(new[] { "acme.app" }));

        // Cascading disable shuts down consumer and dependency cleanly.
        var beforeDisable = Volatile.Read(ref stateChangedCount[0]);
        var cascadeDisable = await manager.SetEnabledAsync("acme.lib", isEnabled: false, shouldCascadeDependents: true);
        Assert.That(cascadeDisable.Status, Is.EqualTo(ModOperationStatus.Applied));
        Assert.That(Volatile.Read(ref stateChangedCount[0]), Is.EqualTo(beforeDisable + 1));
        Assert.That(manager.InstalledMods.All(mod => !mod.IsEnabled), Is.True);

        // Re-enabling acme.app requires confirmation when its dependency acme.lib is disabled, then succeeds with cascade.
        var enableGuard = await manager.SetEnabledAsync("acme.app", isEnabled: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enableGuard.Status, Is.EqualTo(ModOperationStatus.ConfirmationRequired));
            Assert.That(enableGuard.AffectedDependencies, Is.EqualTo(new[] { "acme.lib" }));
        }

        var beforeEnable = Volatile.Read(ref stateChangedCount[0]);
        var enableApp = await manager.SetEnabledAsync("acme.app", isEnabled: true, shouldCascadeDependencies: true);
        Assert.That(enableApp.Status, Is.EqualTo(ModOperationStatus.Applied));
        Assert.That(Volatile.Read(ref stateChangedCount[0]), Is.EqualTo(beforeEnable + 1));
        Assert.That(manager.InstalledMods.All(mod => mod.IsEnabled && mod.State == ModState.Active), Is.True);

        // Upgrade acme.lib via zip and roll back to previous version.
        Assert.That((await manager.ImportZipAsync(lib110ZipPath)).Status, Is.EqualTo(ModOperationStatus.Applied));
        var upgraded = manager.InstalledMods.Single(mod => mod.Id == "acme.lib");
        Assert.That(upgraded.Version, Is.EqualTo("1.1.0"));
        Assert.That(upgraded.HasRollback, Is.True);

        Assert.That((await manager.RollbackAsync("acme.lib")).Status, Is.EqualTo(ModOperationStatus.Applied));
        var rolledBack = manager.InstalledMods.Single(mod => mod.Id == "acme.lib");
        Assert.That(rolledBack.Version, Is.EqualTo("1.0.0"));
        Assert.That(rolledBack.HasRollback, Is.False);

        // Uninstalling acme.lib without cascade requires confirmation; cascading removes both cleanly.
        var uninstallGuard = await manager.UninstallAsync("acme.lib");
        Assert.That(uninstallGuard.Status, Is.EqualTo(ModOperationStatus.ConfirmationRequired));
        Assert.That(uninstallGuard.AffectedDependents, Is.EqualTo(new[] { "acme.app" }));

        var cascadeUninstall = await manager.UninstallAsync("acme.lib", shouldDeleteData: true, shouldCascadeDependents: true);
        Assert.That(cascadeUninstall.Status, Is.EqualTo(ModOperationStatus.Applied));
        Assert.That(manager.InstalledMods, Is.Empty);
    }

    [Test]
    public async Task SetEnabledAsync_WhenMissingDependencyAvailableInSource_ReturnsConfirmationWithoutSilentDownload()
    {
        await using var harness = new Harness();
        var depZip = CreateFixtureZipBytes("acme.dep", "1.0.0");
        var appDir = Path.Combine(harness.Root, "dev-app");
        WriteFixtureDirectory(appDir, "acme.app", "1.0.0", [new ModDependency { Id = "acme.dep", Version = "[1.0.0,2.0.0)", Assemblies = [] }]);

        var repoResources = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["index.json"] = CreateDirectoryIndexBytes("custom", ("acme.dep", "1.0.0", "packages/acme.dep-1.0.0.zip", depZip, false)),
            ["packages/acme.dep-1.0.0.zip"] = depZip
        };
        var source = CreateEmbeddedSource("custom", repoResources);

        await using (var manager1 = harness.CreateManager(builtInSources: [source]))
        {
            await manager1.InitializeAsync(CancellationToken.None);
            Assert.That((await manager1.InstallAsync("acme.dep")).Status, Is.EqualTo(ModOperationStatus.Applied));
            Assert.That((await manager1.ImportDirectoryAsync(appDir)).Status, Is.EqualTo(ModOperationStatus.Applied));
            Assert.That(
                (await manager1.SetEnabledAsync("acme.dep", isEnabled: false, shouldCascadeDependents: true)).Status,
                Is.EqualTo(ModOperationStatus.Applied));
        }

        // Remove acme.dep from mods-state.json so acme.app remains installed/disabled while acme.dep must be downloaded.
        var statePath = harness.Options.StateFilePath ?? throw new InvalidOperationException("Missing state file path.");
        var stateJson = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
            ?? throw new InvalidOperationException("Invalid test mods-state.json.");
        var modsNode = stateJson["mods"]?.AsObject() ?? throw new InvalidOperationException("Missing mods property.");
        Assert.That(modsNode.Remove("acme.dep"), Is.True);
        await File.WriteAllTextAsync(statePath, stateJson.ToJsonString());

        await using var manager2 = harness.CreateManager(builtInSources: [source]);
        await manager2.InitializeAsync(CancellationToken.None);

        var enableResult = await manager2.SetEnabledAsync("acme.app", isEnabled: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enableResult.Status, Is.EqualTo(ModOperationStatus.ConfirmationRequired));
            Assert.That(
                enableResult.SolveResult?.Packages.Any(pkg => pkg.Id == "acme.dep" && !pkg.IsAlreadyInstalledVersion),
                Is.True);
            Assert.That(manager2.InstalledMods.Single(m => m.Id == "acme.app").IsEnabled, Is.False);
            Assert.That(manager2.InstalledMods.Any(m => m.Id == "acme.dep"), Is.False);
        }
    }

    [Test]
    public async Task DisposeAsync_CalledTwiceDirectlyAndThroughHostServiceContainer_IsIdempotent()
    {
        await using var harness = new Harness();

        var directManager = harness.CreateManager();
        await directManager.DisposeAsync();
        await directManager.DisposeAsync();

        // Mirrors ServiceExtensions.AddModding(), where Autofac's Disposer receives both ModManager and IModManager.
        var options = harness.CreateOptions();
        var services = new ServiceCollection()
            .AddSingleton(sp => new ModManager(sp, options))
            .AddSingleton<IModManager>(sp => sp.GetRequiredService<ModManager>());
        var container = new HostServiceContainer(services);
        _ = container.GetRequiredService<IModManager>();

        await container.DisposeAsync();
    }

    [Test]
    public async Task SetEnabledAsync_WhenHotUnloadRequiresRestart_MarksDisabledModPendingRestart()
    {
        await using var harness = new Harness();
        var modDir = Path.Combine(harness.Root, "dev", "acme.sticky");
        WriteFixtureDirectory(
            modDir,
            "acme.sticky",
            "1.0.0",
            entryType: "Everywhere.Core.Tests.Modding.Fixtures.FailingStopMod");

        await using var manager = harness.CreateManager();
        await manager.InitializeAsync(CancellationToken.None);
        Assert.That((await manager.ImportDirectoryAsync(modDir)).Status, Is.EqualTo(ModOperationStatus.Applied));

        var disableResult = await manager.SetEnabledAsync("acme.sticky", isEnabled: false);
        var summary = manager.InstalledMods.Single(m => m.Id == "acme.sticky");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(disableResult.Status, Is.EqualTo(ModOperationStatus.RestartRequired));
            Assert.That(manager.IsRestartRequired, Is.True);
            Assert.That(summary.IsEnabled, Is.False);
            Assert.That(summary.IsPendingRestart, Is.True);
        }
    }

    private static EmbeddedModSource CreateEmbeddedSource(string id, Dictionary<string, byte[]> resources) =>
        new(
            id,
            relativePath => resources.TryGetValue(relativePath, out var bytes) ? new MemoryStream(bytes, writable: false) : null,
            isSignatureRequired: false);

    private static byte[] CreateDirectoryIndexBytes(
        string sourceId,
        params (string Id, string Version, string ArtifactUrl, byte[] ZipBytes, bool IsDefaultEnabled)[] packages
    )
    {
        var entries = packages.Select(pkg => new ModIndexEntry
            {
                Id = pkg.Id,
                Name = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = pkg.Id },
                Description = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Test fixture" },
                IsDefaultEnabled = pkg.IsDefaultEnabled,
                Channels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stable"] = pkg.Version },
                Versions =
                [
                    new ModIndexVersion
                    {
                        Version = pkg.Version,
                        PublishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Entry = new UniversalModEntry
                        {
                            Assembly = Path.GetFileName(typeof(ModManagerTests).Assembly.Location),
                            Type = "Everywhere.Core.Tests.Modding.Fixtures.CollectibleLifecycleMod",
                            Assemblies = [Path.GetFileName(typeof(ModManagerTests).Assembly.Location)],
                            Framework = "net11.0",
                            ModApiVersion = "[1.0.0,2.0.0)"
                        },
                        Artifact = new ModArtifact
                        {
                            Url = pkg.ArtifactUrl,
                            Size = pkg.ZipBytes.Length,
                            Sha256 = Convert.ToHexStringLower(SHA256.HashData(pkg.ZipBytes))
                        }
                    }
                ]
            })
            .ToArray();

        var index = new ModIndex
        {
            Format = 1,
            Id = sourceId,
            Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = sourceId },
            GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Mods = entries
        };

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(index));
    }

    private static byte[] CreateFixtureZipBytes(
        string id,
        string version,
        ModDependency[]? dependencies = null,
        string readmeContent = "# Readme"
    )
    {
        var staging = Path.Combine(Path.GetTempPath(), "Everywhere.ModManager.Pkg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            WriteFixtureDirectory(staging, id, version, dependencies, readmeContent);
            using var memory = new MemoryStream();
            ZipFile.CreateFromDirectory(staging, memory);
            return memory.ToArray();
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static void WriteFixtureDirectory(
        string directory,
        string id,
        string version,
        ModDependency[]? dependencies = null,
        string readmeContent = "# Readme",
        string entryType = "Everywhere.Core.Tests.Modding.Fixtures.CollectibleLifecycleMod"
    )
    {
        Directory.CreateDirectory(directory);
        var assemblyLocation = typeof(ModManagerTests).Assembly.Location;
        var assemblyFileName = Path.GetFileName(assemblyLocation);
        File.Copy(assemblyLocation, Path.Combine(directory, assemblyFileName), overwrite: true);
        File.WriteAllText(Path.Combine(directory, "README.md"), readmeContent);

        var manifest = new ModManifest
        {
            Id = id,
            Version = version,
            Name = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = id },
            Description = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Lifecycle test mod" },
            Readme = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "README.md" },
            Entry = new UniversalModEntry
            {
                Assembly = assemblyFileName,
                Type = entryType,
                Assemblies = [assemblyFileName],
                Framework = "net11.0",
                ModApiVersion = "[1.0.0,2.0.0)",
                Dependencies = dependencies ?? []
            }
        };
        ModManifestFile.Write(directory, manifest);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Everywhere.ModManager.Tests", Guid.NewGuid().ToString("N"));

        public IList<string> Trace { get; } = new List<string>();

        public ModManagerOptions Options { get; }

        private readonly HostServiceContainer _services;
        private readonly HostAssemblyCatalog _catalog;
        private readonly ModHostEnvironment _host = new("win-x64", "0.9.0", "1.0.0", "net11.0");

        public Harness()
        {
            Directory.CreateDirectory(Root);
            _services = new HostServiceContainer(new ServiceCollection().AddSingleton(Trace));

            var selfLocation = typeof(ModManagerTests).Assembly.Location;
            var hostPaths = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
                .Append(typeof(Porta.Pty.IPtyConnection).Assembly.Location)
                .Where(path => !string.Equals(path, selfLocation, StringComparison.OrdinalIgnoreCase) && IsManagedAssembly(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            _catalog = new HostAssemblyCatalog(hostPaths);
            Options = CreateOptions();
        }

        public ModManagerOptions CreateOptions(string officialUrl = "", string officialPublicKey = "") =>
            new()
            {
                StateFilePath = Path.Combine(Root, "state", ModStateStore.DefaultFileName),
                PackageStoreDirectory = Path.Combine(Root, "cache", "packages"),
                DataDirectory = Path.Combine(Root, "data"),
                StagingDirectory = Path.Combine(Root, "cache", "staging"),
                HttpCacheDirectory = Path.Combine(Root, "cache", "http"),
                HostEnvironment = _host,
                HostAssemblyCatalog = _catalog,
                OfficialSourceUrl = officialUrl,
                OfficialSourceMirrors = string.Empty,
                OfficialSourcePublicKey = officialPublicKey,
                EngineOptions = new ModEngineOptions
                {
                    CollectionAttempts = 20,
                    CollectionDelay = TimeSpan.FromMilliseconds(25)
                }
            };

        public ModManager CreateManager(
            IEnumerable<IModRepositorySource>? builtInSources = null,
            string officialUrl = "",
            string officialPublicKey = ""
        ) =>
            new(_services, CreateOptions(officialUrl, officialPublicKey), builtInSources);

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static bool IsManagedAssembly(string path)
        {
            try
            {
                AssemblyName.GetAssemblyName(path);
                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }
    }
}
