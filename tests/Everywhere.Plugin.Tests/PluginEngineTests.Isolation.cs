using Everywhere.Plugin.Abstractions;
using Everywhere.Plugin.Hosting;
using Everywhere.Plugin.Hosting.Installation;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Tests;

public sealed partial class PluginEngineTests
{
    /// <summary>
    /// Invalid entry declarations and damaged files remain package failures after a successful import.
    /// Selected consumers are blocked while an independent plugin can start and retire normally.
    /// </summary>
    [TestCase("satellite", "InvalidEntryType")]
    [TestCase("missing-type", "InvalidEntryType")]
    [TestCase("damaged", "PreflightFailed")]
    public async Task ApplyAsync_InvalidImportedEntry_BlocksConsumersAndStartsIndependentPlugin(string scenario, string expectedCode)
    {
        var original = Definition("B");
        var source = Path.Combine(_dataDirectory, "source");
        Directory.CreateDirectory(source);
        File.Copy(Path.Combine(original.Installation.Directory, "PluginFixture.B.dll"), Path.Combine(source, "PluginFixture.B.dll"));
        var entry = original.Entry;

        switch (scenario)
        {
            case "satellite":
            {
                File.Copy(Path.Combine(original.Installation.Directory, "PluginFixture.B.resources.dll"), Path.Combine(source, "translation.dll"));
                entry = entry with { Assembly = "translation.dll", Assemblies = ["PluginFixture.B.dll", "translation.dll"] };
                break;
            }
            case "missing-type":
            {
                entry = entry with { Type = "Missing.Entry" };
                break;
            }
            case "damaged":
            {
                using var stream = File.OpenWrite(Path.Combine(source, "PluginFixture.B.dll"));
                stream.SetLength(16);
                break;
            }
        }

        PluginManifestFile.Write(source, original.Installation.Manifest with { Entry = entry });
        var store = new PluginPackageStore(
            Path.Combine(_dataDirectory, "installed"),
            Path.Combine(_dataDirectory, "data"),
            Path.Combine(_dataDirectory, "staging"));
        var installation = await store.ImportDirectoryAsync(source);
        var invalid = new PluginDefinition(installation, store.GetDataDirectory(installation), "win-x64", "1.0.0", "1.0.0", "net10.0");
        var services = new Services();
        var engine = CreateEngine(services);

        var result = await engine.ApplyAsync([Definition("A"), invalid, ResourceDefinition("independent")]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.PluginId == invalid.Id && diagnostic.Code == expectedCode), Is.True);
        Assert.That(engine.Plugins.Single(plugin => plugin.Id == invalid.Id).State, Is.EqualTo(PluginState.Failed));
        Assert.That(engine.Plugins.Single(plugin => plugin.Id == "fixture.a").State, Is.EqualTo(PluginState.Blocked));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "resource.independent" }));
        Assert.That(services.Events, Does.Not.Contain("initialize:fixture.a"));
        Assert.That(services.Events, Does.Not.Contain("initialize:fixture.b"));
        Assert.That(result.ResidentAssemblyNames, Is.EqualTo(new[] { "PluginFixture.Resources" }));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Selected optional providers remain dependencies when initialization fails. Unrelated
    /// activations on either side of the failure keep running, including shared physical owners.
    /// </summary>
    [Test]
    public async Task ApplyAsync_ProviderFails_BlocksTransitiveConsumersAndKeepsIndependentPlugins()
    {
        var services = new Services { Modes = id => id == "fixture.b" ? "initialize-failure" : "normal" };
        var engine = CreateEngine(services);
        var first = Definition("B", "fixture.0");
        var last = Definition("B", "fixture.z");
        var dependent = WithEntry(ResourceDefinition("consumer"), ResourceDefinition("consumer").Entry with
        {
            Dependencies = [new PluginDependency { Id = "fixture.a", Version = "[1.0,2.0)", Assemblies = [] }]
        });

        var result = await engine.ApplyAsync([first, Definition("A"), Definition("B"), dependent, last]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "fixture.0", "fixture.z" }));
        Assert.That(engine.Plugins.Single(plugin => plugin.Id == "fixture.b").State, Is.EqualTo(PluginState.Failed));
        Assert.That(engine.Plugins.Where(plugin => plugin.Id is "fixture.a" or "resource.consumer")
            .All(plugin => plugin.State == PluginState.Blocked), Is.True);
        Assert.That(services.Events, Does.Not.Contain("initialize:fixture.a"));
        Assert.That(services.Events, Does.Not.Contain("entry-dispose:fixture.0"));
        Assert.That(result.ResidentAssemblyNames, Is.EqualTo(new[] { "PluginFixture.B" }));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Failure of one consumer must not stop its provider or a sibling consumer of that provider.
    /// </summary>
    [Test]
    public async Task ApplyAsync_ConsumerFails_KeepsProviderAndSiblingActive()
    {
        var services = new Services { Modes = id => id == "fixture.a" ? "initialize-failure" : "normal" };
        var engine = CreateEngine(services);
        var result = await engine.ApplyAsync([Definition("A"), Definition("B"), Definition("A", "fixture.d")]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "fixture.b", "fixture.d" }));
        Assert.That(services.Events, Does.Contain("optional:42"));
        Assert.That(services.Events, Does.Not.Contain("entry-dispose:fixture.b"));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// A failed entry's static resources belong to the shared DLL, not that individual activation.
    /// </summary>
    [Test]
    public async Task ApplyAsync_SharedEntryConstructorFails_KeepsStaticResourcesUntilLastUserStops()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var result = await engine.ApplyAsync([ResourceDefinition("a", "FailedEntry"), ResourceDefinition("b")]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "resource.b" }));
        Assert.That(services.Events.Any(value => value.StartsWith("assembly:", StringComparison.Ordinal)), Is.False);
        AssertApplied(await engine.ApplyAsync([]));
        Assert.That(services.Events, Does.Contain("assembly:failed-constructor"));
        Assert.That(services.Events, Does.Contain("assembly:module"));
    }

    /// <summary>
    /// Invalid updates leave stopped predecessors inactive while an independent new plugin starts.
    /// </summary>
    [Test]
    public async Task ApplyAsync_InvalidReplacement_StopsOldProviderAndDoesNotFallBack()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var a = Definition("A");
        var b = Definition("B");
        AssertApplied(await engine.ApplyAsync([a, b]));
        services.Events.Clear();
        var invalid = WithEntry(b, b.Entry with { Type = "Missing.Entry" }, "replacement");

        var result = await engine.ApplyAsync([a, invalid, ResourceDefinition("independent")]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "resource.independent" }));
        Assert.That(services.Events.IndexOf("entry-dispose:fixture.a"), Is.LessThan(services.Events.IndexOf("entry-dispose:fixture.b")));
        Assert.That(services.Events, Does.Not.Contain("initialize:fixture.b"));
        Assert.That(engine.Plugins.Single(plugin => plugin.Id == "fixture.a").State, Is.EqualTo(PluginState.Blocked));
        Assert.That(result.ResidentAssemblyNames, Is.EqualTo(new[] { "PluginFixture.Resources" }));
        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.PluginId == "fixture.b" && diagnostic.Code == "InvalidEntryType"), Is.True);
        AssertApplied(await engine.ApplyAsync([a, b]));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Once shutdown starts, a failed replacement does not resurrect old code or roll back unrelated work.
    /// </summary>
    [Test]
    public async Task ApplyAsync_ReplacementInitializationFails_CleansNewGenerationWithoutRestoringOldOne()
    {
        var shouldFail = false;
        var services = new Services { Modes = id => shouldFail && id == "fixture.b" ? "initialize-failure" : "normal" };
        var engine = CreateEngine(services);
        var a = Definition("A");
        var b = Definition("B");
        AssertApplied(await engine.ApplyAsync([a, b]));
        services.Events.Clear();
        shouldFail = true;
        var replacement = WithEntry(b, b.Entry, "replacement");

        var result = await engine.ApplyAsync([a, replacement, ResourceDefinition("independent")]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "resource.independent" }));
        Assert.That(result.ResidentAssemblyNames, Is.EqualTo(new[] { "PluginFixture.Resources" }));
        Assert.That(services.Events.Count(value => value == "initialize:fixture.b"), Is.EqualTo(1));
        Assert.That(services.Events, Does.Not.Contain("initialize:fixture.a"));
        shouldFail = false;
        AssertApplied(await engine.ApplyAsync([a, replacement]));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// A logical cycle invalidates its members and consumers, not the independent graph.
    /// </summary>
    [Test]
    public async Task ApplyAsync_DependencyCycle_RejectsCycleAndStartsIndependentPlugin()
    {
        var a = Definition("B", "cycle.a");
        var b = Definition("B", "cycle.b");
        a = WithEntry(a, a.Entry with { Dependencies = [new PluginDependency { Id = b.Id, Version = "[1.0,2.0)", Assemblies = [] }] });
        b = WithEntry(b, b.Entry with { Dependencies = [new PluginDependency { Id = a.Id, Version = "[1.0,2.0)", Assemblies = [] }] });
        var engine = CreateEngine(new Services());

        var result = await engine.ApplyAsync([a, b, ResourceDefinition("independent")]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "resource.independent" }));
        Assert.That(result.Diagnostics.Count(diagnostic => diagnostic.Code == "PluginCycle"), Is.EqualTo(2));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Host incompatibility is a package failure, not an instruction to silently deselect an optional provider.
    /// </summary>
    [Test]
    public async Task ApplyAsync_IncompatibleHost_BlocksSelectedConsumersAndStartsIndependentPlugin()
    {
        var b = Definition("B");
        b = WithEntry(b, b.Entry with { HostVersion = "[2.0,3.0)" });
        var engine = CreateEngine(new Services());

        var result = await engine.ApplyAsync([Definition("A"), b, ResourceDefinition("independent")]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "resource.independent" }));
        Assert.That(engine.Plugins.Single(plugin => plugin.Id == "fixture.a").State, Is.EqualTo(PluginState.Blocked));
        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.PluginId == "fixture.b" && diagnostic.Code == "IncompatibleHost"), Is.True);
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Different localization payloads for one shared assembly are conflicting providers too.
    /// Neither enumeration order nor a healthy independent branch chooses a winning provider.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ApplyAsync_SharedPayloadConflict_RejectsBothProvidersAndTheirConsumers(bool shouldReverse)
    {
        var first = Definition("B");
        var second = Definition("B", "fixture.second");
        second = WithEntry(second, second.Entry with { Assemblies = ["PluginFixture.B.dll", "PluginFixture.B.resources.dll"] });
        var engine = CreateEngine(new Services());
        var desired = new[] { Definition("A"), first, second, ResourceDefinition("independent") };
        if (shouldReverse) Array.Reverse(desired);

        var result = await engine.ApplyAsync(desired);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "resource.independent" }));
        Assert.That(result.Diagnostics.Count(diagnostic => diagnostic.Code == "AssemblyConflict"), Is.EqualTo(2));
        Assert.That(engine.Plugins.Single(plugin => plugin.Id == "fixture.a").State, Is.EqualTo(PluginState.Blocked));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// A failed sibling update cannot freeze a stable provider, another update or a new consumer.
    /// </summary>
    [Test]
    public async Task ApplyAsync_InvalidSiblingUpdate_AllowsOtherUpdateAndNewConsumer()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var a = Definition("A");
        var b = Definition("B");
        var d = Definition("A", "fixture.d");
        AssertApplied(await engine.ApplyAsync([a, b, d]));
        services.Events.Clear();

        var result = await engine.ApplyAsync([
            WithEntry(a, a.Entry with { Type = "Missing.Entry" }, "a2"), b,
            WithEntry(d, d.Entry, "d2"), Definition("A", "fixture.e")
        ]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "fixture.b", "fixture.d", "fixture.e" }));
        Assert.That(services.Events, Does.Not.Contain("entry-dispose:fixture.b"));
        Assert.That(services.Events, Does.Contain("initialize:fixture.d"));
        Assert.That(services.Events, Does.Contain("initialize:fixture.e"));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Replacing logical entries does not retire unchanged static ownership still needed by a survivor.
    /// </summary>
    [Test]
    public async Task ApplyAsync_SharedOwnerUpdateFails_KeepsUnchangedAssemblyAndUpdatesOtherEntry()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var a = ResourceDefinition("a");
        var c = ResourceDefinition("c");
        var keeper = ResourceDefinition("keeper");
        AssertApplied(await engine.ApplyAsync([a, c, keeper]));
        services.Events.Clear();

        var result = await engine.ApplyAsync([
            WithEntry(a, a.Entry with { Type = "Missing.Entry" }, "a2"), WithEntry(c, c.Entry, "c2"), keeper
        ]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "resource.c", "resource.keeper" }));
        Assert.That(services.Events, Does.Contain("activation:resource.a"));
        Assert.That(services.Events, Does.Contain("initialize:resource.c"));
        Assert.That(services.Events, Does.Not.Contain("activation:resource.keeper"));
        Assert.That(services.Events.Any(value => value.StartsWith("assembly:", StringComparison.Ordinal)), Is.False);
        AssertApplied(await engine.ApplyAsync([]));
        Assert.That(services.Events.Count(value => value == "assembly:module"), Is.EqualTo(1));
    }

    /// <summary>
    /// A new incompatible provider cannot expand retirement to a running plugin or its consumers.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ApplyAsync_NewSharedProviderConflicts_LeavesRunningOwnersUntouched(bool hasBindingConflict)
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var a = Definition("A");
        var b = Definition("B");
        var newcomer = Definition("B", "fixture.new");
        newcomer = WithEntry(newcomer, hasBindingConflict ? newcomer.Entry with
        {
            Dependencies = [new PluginDependency { Id = "resource.independent", Version = "[1.0,2.0)", Assemblies = ["PluginFixture.Resources"] }]
        } : newcomer.Entry with { Assemblies = ["PluginFixture.B.dll", "PluginFixture.B.resources.dll"] });
        AssertApplied(await engine.ApplyAsync([a, b]));
        services.Events.Clear();

        var result = await engine.ApplyAsync([a, b, newcomer, ResourceDefinition("independent")]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "fixture.a", "fixture.b", "resource.independent" }));
        Assert.That(services.Events, Is.EqualTo(new[] { "initialize:resource.independent" }));
        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.PluginId == newcomer.Id &&
            diagnostic.Code == (hasBindingConflict ? "SharedBindingConflict" : "AssemblyConflict")), Is.True);
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Explicit removal cannot be undone to preserve the predecessor of an invalid replacement.
    /// </summary>
    [Test]
    public async Task ApplyAsync_InvalidUpdateAndDependencyRemoval_RemovesBothOldInstances()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var a = Definition("A");
        AssertApplied(await engine.ApplyAsync([a, Definition("B")]));
        services.Events.Clear();

        var result = await engine.ApplyAsync([
            WithEntry(a, a.Entry with { Type = "Missing.Entry" }, "a2"), ResourceDefinition("independent")
        ]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "resource.independent" }));
        Assert.That(result.ResidentAssemblyNames, Is.EqualTo(new[] { "PluginFixture.Resources" }));
        Assert.That(services.Events.IndexOf("entry-dispose:fixture.a"), Is.LessThan(services.Events.IndexOf("entry-dispose:fixture.b")));
        Assert.That(engine.Plugins.Single(plugin => plugin.Id == "fixture.b").IsEnabled, Is.False);
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Neither disposal failure nor unconfirmed collection permits entering the loading stage.
    /// </summary>
    [TestCase("disposal-failure")]
    [TestCase("retained")]
    public async Task ApplyAsync_RetirementIsUnconfirmed_DoesNotPrepareOrInitializeNewPlugins(string mode)
    {
        var services = new Services { Mode = mode };
        var engine = CreateEngine(services);
        var b = Definition("B");
        AssertApplied(await engine.ApplyAsync([b]));
        services.Events.Clear();

        var result = await engine.ApplyAsync([WithEntry(b, b.Entry, "b2"), ResourceDefinition("new")]);
        services.Retained = null;

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.RestartRequired), Describe(result));
        Assert.That(result.ResidentAssemblyNames, Does.Not.Contain("PluginFixture.Resources"));
        Assert.That(services.Events.Any(value => value.StartsWith("initialize:", StringComparison.Ordinal)), Is.False);
    }

    /// <summary>
    /// Changing shared satellite content retires every old consumer before creating replacement owners.
    /// </summary>
    [Test]
    public async Task ApplyAsync_SharedPayloadChanges_RetiresConsumersBeforeReinitializing()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var first = Definition("B");
        var second = Definition("B", "fixture.second");
        AssertApplied(await engine.ApplyAsync([first, second]));
        services.Events.Clear();
        services.Mode = "resources";
        first = WithEntry(first, first.Entry with { Assemblies = ["PluginFixture.B.dll", "PluginFixture.B.resources.dll"] }, "b2");
        second = WithEntry(second, second.Entry with { Assemblies = ["PluginFixture.B.dll", "PluginFixture.B.resources.dll"] }, "second2");

        AssertApplied(await engine.ApplyAsync([first, second]));

        var initialized = services.Events.IndexOf("initialize:fixture.b");
        Assert.That(services.Events.IndexOf("entry-dispose:fixture.b"), Is.LessThan(initialized));
        Assert.That(services.Events.IndexOf("entry-dispose:fixture.second"), Is.LessThan(initialized));
        Assert.That(services.Events.Count(value => value == "resource:French"), Is.EqualTo(2));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Adding a native provider cannot mutate a live resolver. An explicit replacement of the
    /// last current user can retire that owner and permit the newly selected provider set.
    /// </summary>
    [Test]
    [Platform("Win")]
    public async Task ApplyAsync_NewNativeProvider_RequiresExplicitRetirementOfResidentOwner()
    {
        var services = new Services { Mode = "native-probe" };
        var engine = CreateEngine(services);
        var first = SharedNativeDefinition("first", "version.dll");
        var second = SharedNativeDefinition("second", "version.dll");
        AssertApplied(await engine.ApplyAsync([first]));
        services.Events.Clear();

        var result = await engine.ApplyAsync([first, second]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ActivePluginIds, Is.EqualTo(new[] { "fixture.first" }));
        Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("ResidentNativeBindingConflict"));
        Assert.That(services.Events, Is.Empty);
        AssertApplied(await engine.ApplyAsync([WithEntry(first, first.Entry, "first2"), second]));
        Assert.That(services.Events.Count(value => value == "native:0"), Is.EqualTo(2));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Changes package metadata through the same definition boundary as an installed replacement.
    /// </summary>
    private static PluginDefinition WithEntry(PluginDefinition original, UniversalPluginEntry entry, string? installationId = null)
    {
        return new PluginDefinition(original.Installation with
        {
            InstallationId = installationId ?? original.Installation.InstallationId,
            Manifest = original.Installation.Manifest with { Entry = entry }
        }, original.DataDirectory, "win-x64", "1.0.0", "1.0.0", "net10.0");
    }
}
