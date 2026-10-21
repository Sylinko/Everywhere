using System.Diagnostics;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Tests;

public sealed partial class ModEngineTests
{
    /// <summary>
    /// An unsupported package stays enabled and observable while an independent consumer treats it as unavailable.
    /// </summary>
    [Test]
    public async Task ApplyAsync_UnsupportedOptionalDependency_SkipsWithoutInspectingForeignCode()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var skipped = PlatformDefinition("osx-arm64");
        AssertApplied(await engine.ApplyAsync([Definition("A"), skipped]));
        Assert.That(services.Events, Does.Contain("optional:1"));
        var info = engine.Mods.Single(mod => mod.Id == "fixture.b");
        Assert.That(info.State, Is.EqualTo(ModState.Skipped));
        Assert.That(info.IsEnabled, Is.True);
        Assert.That(info.SkipReason, Is.EqualTo(ModSkipReason.UnsupportedPlatform));
        Assert.That(services.Events, Does.Not.Contain("initialize:fixture.b"));
        AssertApplied(await engine.ApplyAsync([]));
        Assert.That(engine.Mods.Single(mod => mod.Id == "fixture.b").State, Is.EqualTo(ModState.Inactive));
    }

    /// <summary>
    /// Platform skipping does not satisfy required dependencies or weaken preflight failure handling.
    /// </summary>
    [Test]
    public async Task ApplyAsync_UnsupportedRequiredDependency_RejectsBeforeActivation()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var consumer = Definition("A");
        consumer.Entry.Dependencies = [consumer.Entry.Dependencies[0] with { IsOptional = false }];
        var result = await engine.ApplyAsync([consumer, PlatformDefinition("osx-arm64")]);
        Assert.That(result.Status, Is.EqualTo(ModApplyStatus.CompletedWithErrors));
        Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("MissingDependency"));
        Assert.That(services.Events, Is.Empty);
    }

    /// <summary>
    /// Unused native assets never enter PE validation or native loading.
    /// </summary>
    [Test]
    public async Task ApplyAsync_UnusedNativeLibrary_DoesNotLoadOrValidateItsBinary()
    {
        Directory.CreateDirectory(_dataDirectory);
        File.WriteAllText(Path.Combine(_dataDirectory, "unused.dll"), "Not executable code.");
        var definition = NativeDefinition("unused.dll");
        var engine = CreateEngine(new Services());
        AssertApplied(await engine.ApplyAsync([definition]));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Uses an existing Windows library under a private filename to exercise real lazy native resolution.
    /// This avoids introducing a compiler dependency or distributing a system binary in the test repository.
    /// </summary>
    [Test]
    [Platform("Win")]
    public async Task ApplyAsync_NativeImport_ResolvesPackageFileAndCollectsManagedOwner()
    {
        Directory.CreateDirectory(_dataDirectory);
        var nativePath = Path.Combine(_dataDirectory, "fixture_native.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), nativePath);
        var services = new Services { Mode = "native-probe" };
        var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([NativeDefinition("fixture_native.dll")]));
        Assert.That(services.Events, Does.Contain("native:0"));
        AssertApplied(await engine.ApplyAsync([]));
        using var process = Process.GetCurrentProcess();
        var isLoaded = process.Modules.Cast<ProcessModule>().Any(module => module.FileName.Equals(nativePath, StringComparison.OrdinalIgnoreCase));
        Assert.That(isLoaded, Is.False, "The native module must not outlive its collected owner.");
    }

    /// <summary>
    /// Native disagreement is reported only when the shared wrapper actually requests the library.
    /// A provider without a native asset must not silently borrow another provider's private DLL.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    [Platform("Win")]
    public async Task ApplyAsync_SharedNativeProvidersDisagree_FailsAtFirstImportAndCleansUp(bool hasSecondLibrary)
    {
        var first = SharedNativeDefinition("first", "version.dll");
        var second = SharedNativeDefinition("second", hasSecondLibrary ? "winmm.dll" : null);
        var services = new Services();
        await using var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([first, second]));
        AssertApplied(await engine.ApplyAsync([]));

        services.Mode = "native-probe";
        var failed = await engine.ApplyAsync([first, second]);
        Assert.That(failed.Status, Is.EqualTo(ModApplyStatus.CompletedWithErrors), Describe(failed));
        Assert.That(Describe(failed), Does.Contain("providers disagree"));
        Assert.That(failed.ResidentAssemblyNames, Is.Empty);
    }

    /// <summary>
    /// Identical native assets permit sharing. Removing a provider reconstructs consumers before changing
    /// the owner's resolution environment, and releases the old provider's native loader reference.
    /// </summary>
    [Test]
    [Platform("Win")]
    public async Task ApplyAsync_SharedNativeProviderRemoved_RecreatesRemainingConsumerAndReleasesBothLibraries()
    {
        var first = SharedNativeDefinition("first", "version.dll");
        var second = SharedNativeDefinition("second", "version.dll");
        var services = new Services { Mode = "native-probe" };
        await using var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([first, second]));
        services.Events.Clear();
        AssertApplied(await engine.ApplyAsync([second]));
        Assert.That(services.Events, Does.Contain("entry-dispose:fixture.second"));
        Assert.That(services.Events, Does.Contain("initialize:fixture.second"));
        Assert.That(services.Events, Does.Contain("native:0"));
        AssertApplied(await engine.ApplyAsync([]));

        using var process = Process.GetCurrentProcess();
        Assert.That(process.Modules.Cast<ProcessModule>().Any(module => module.FileName.StartsWith(_dataDirectory, StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    /// <summary>
    /// Creates independent package providers of the same managed wrapper with selected native contents.
    /// </summary>
    private ModDefinition SharedNativeDefinition(string name, string? systemLibrary)
    {
        var original = Definition("B", "fixture." + name);
        var directory = Path.Combine(_dataDirectory, name);
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(original.Installation.Directory, original.Entry.Assembly), Path.Combine(directory, original.Entry.Assembly));
        if (systemLibrary is not null)
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, systemLibrary), Path.Combine(directory, "fixture_native.dll"));
        }

        return new ModDefinition(new ModInstallation
        {
            InstallationId = name,
            Directory = directory,
            Manifest = original.Installation.Manifest with
            {
                Entry = original.Entry with { NativeLibraries = systemLibrary is null ? [] : ["fixture_native.dll"] }
            }
        }, directory, "win-x64", "1.0.0", "1.0.0", "net10.0");
    }

    /// <summary>
    /// Creates a valid declaration whose foreign DLL deliberately contains no managed metadata.
    /// </summary>
    private ModDefinition PlatformDefinition(string runtimeIdentifier)
    {
        Directory.CreateDirectory(_dataDirectory);
        File.WriteAllText(Path.Combine(_dataDirectory, "foreign.dll"), "Foreign platform payload.");
        var manifest = new ModManifest
        {
            Id = "fixture.b", Version = "1.0.0",
            Entry = new PlatformModEntry
            {
                Variants = [new ModPlatformVariant
                {
                    Platform = "win-x64", Assembly = "foreign.dll", Type = "ModFixture.Entry",
                    Framework = "net10.0", ModApiVersion = "[1.0,2.0)", Assemblies = ["foreign.dll"]
                }]
            }
        };
        return new ModDefinition(new ModInstallation
        {
            InstallationId = "foreign", Directory = _dataDirectory, Manifest = manifest
        }, _dataDirectory, runtimeIdentifier, "1.0.0", "1.0.0", "net10.0");
    }

    /// <summary>
    /// Copies a real managed entry alongside explicit native output in a test-owned installation.
    /// </summary>
    private ModDefinition NativeDefinition(string nativeFile)
    {
        var original = Definition("B");
        File.Copy(Path.Combine(original.Installation.Directory, original.Entry.Assembly), Path.Combine(_dataDirectory, original.Entry.Assembly), true);
        return new ModDefinition(new ModInstallation
        {
            InstallationId = "native", Directory = _dataDirectory,
            Manifest = original.Installation.Manifest with { Entry = original.Entry with { NativeLibraries = [nativeFile] } }
        }, _dataDirectory, "win-x64", "1.0.0", "1.0.0", "net10.0");
    }
}