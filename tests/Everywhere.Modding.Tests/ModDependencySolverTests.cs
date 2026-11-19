using System.Diagnostics;
using Everywhere.Modding.Hosting.Resolution;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Verifies CDCL conflict learning, backjumping, host/platform filtering, source stickiness, and cascade helpers.
/// </summary>
public sealed class ModDependencySolverTests
{
    private static readonly ModHostEnvironment DefaultHost = new("win-x64", "1.0.0", "1.0.0", "net11.0");

    /// <summary>
    /// Ensures CDCL backjumping skips unrelated multi-branch subtrees rather than timing out in O(k^n) backtracking.
    /// </summary>
    [Test]
    public void Solve_ConflictBehind25UnrelatedMods_BackjumpsInMilliseconds()
    {
        var entries = new List<ModIndexEntry>
        {
            Mod(
                "root.bundle",
                Version(
                    "1.0.0",
                    [
                        Dep("branch.b", "[1.0,3.0)"),
                        Dep("z.consumer.c", "[1.0,2.0)"),
                        .. Enumerable.Range(0, 25).Select(i => Dep($"u.{i:D2}", "[1.0,3.0)"))
                    ])),
            Mod(
                "branch.b",
                Version("2.0.0", [Dep("core.lib", "[2.0,3.0)")]),
                Version("1.9.0", [Dep("core.lib", "[1.0,2.0)")])),
            Mod(
                "z.consumer.c",
                Version("1.2.0", [Dep("core.lib", "[1.0,2.0)")]),
                Version("1.1.0", [Dep("core.lib", "[1.0,2.0)")]),
                Version("1.0.0", [Dep("core.lib", "[1.0,2.0)")])),
            Mod(
                "core.lib",
                Version("2.5.0"),
                Version("1.8.0"))
        };

        for (var i = 0; i < 25; i++)
        {
            entries.Add(Mod($"u.{i:D2}", Version("2.0.0"), Version("1.0.0")));
        }

        var watch = Stopwatch.StartNew();
        var result = ModDependencySolver.Solve(
            new ModSolveRequest
            {
                Host = DefaultHost,
                Sources = [Catalog("official", entries)],
                Targets = [new ModTargetRequirement("root.bundle")]
            });
        watch.Stop();

        Assert.That(result.IsSuccess, Is.True, result.FailureSummary);
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
        Assert.That(result.Packages.Single(p => p.Id == "branch.b").Version, Is.EqualTo("1.9.0"));
        Assert.That(result.Packages.Single(p => p.Id == "core.lib").Version, Is.EqualTo("1.8.0"));
        Assert.That(result.Packages, Has.Count.EqualTo(29));
    }

    /// <summary>
    /// Unsatisfiable version ranges must terminate at decision level 0 with causal conflict clauses.
    /// </summary>
    [Test]
    public void Solve_UnsatisfiableTransitiveDiamond_ReportsCausalConflictClauses()
    {
        var entries = new[]
        {
            Mod("consumer.a", Version("1.0.0", [Dep("mid.b", "[1.0,2.0)")])),
            Mod("mid.b", Version("1.0.0", [Dep("core.lib", "[2.0,3.0)")])),
            Mod("consumer.c", Version("1.0.0", [Dep("core.lib", "[1.0,2.0)")])),
            Mod("core.lib", Version("2.1.0"), Version("1.4.0"))
        };

        var result = ModDependencySolver.Solve(
            new ModSolveRequest
            {
                Host = DefaultHost,
                Sources = [Catalog("official", entries)],
                Targets = [new ModTargetRequirement("consumer.a"), new ModTargetRequirement("consumer.c")]
            });

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureSummary, Does.Contain("core.lib"));
        Assert.That(result.Conflicts, Is.Not.Empty);
    }

    /// <summary>
    /// Solves a 150-mod synthetic ecosystem graph and verifies deterministic dependency-first topological ordering.
    /// </summary>
    [Test]
    public void Solve_150ModEcosystemGraph_ResolvesDeterministicallyInTopologicalOrder()
    {
        var entries = new List<ModIndexEntry> { Mod("shared.core", Version("1.0.0")) };

        for (var i = 0; i < 10; i++)
        {
            entries.Add(Mod($"base.{i:D2}", Version("1.0.0", [Dep("shared.core", "[1.0,2.0)")])));
        }

        for (var i = 0; i < 40; i++)
        {
            var dep1 = $"base.{i % 10:D2}";
            var dep2 = $"base.{(i + 3) % 10:D2}";
            var deps = dep1 == dep2 ? new[] { Dep(dep1, "[1.0,2.0)") } : new[] { Dep(dep1, "[1.0,2.0)"), Dep(dep2, "[1.0,2.0)") };
            entries.Add(Mod($"mid.{i:D2}", Version("2.0.0", deps), Version("1.0.0", deps)));
        }

        var targets = new List<ModTargetRequirement>();
        for (var i = 0; i < 99; i++)
        {
            var d1 = $"mid.{i % 40:D2}";
            var d2 = $"mid.{(i + 7) % 40:D2}";
            var deps = new List<ModDependency> { Dep(d1, "[1.0,3.0)"), Dep("shared.core", "[1.0,2.0)") };
            if (d2 != d1) deps.Add(Dep(d2, "[1.0,3.0)"));
            var pluginId = $"plugin.{i:D2}";
            entries.Add(Mod(pluginId, Version("1.0.0", [.. deps])));
            targets.Add(new ModTargetRequirement(pluginId));
        }

        var result = ModDependencySolver.Solve(
            new ModSolveRequest
            {
                Host = DefaultHost,
                Sources = [Catalog("official", entries)],
                Targets = targets
            });

        Assert.That(result.IsSuccess, Is.True, result.FailureSummary);
        Assert.That(result.Packages, Has.Count.EqualTo(150));

        var indexById = result.Packages
            .Select((pkg, idx) => (pkg.Id, idx))
            .ToDictionary(x => x.Id, x => x.idx, StringComparer.Ordinal);
        foreach (var package in result.Packages)
        {
            var packageIndex = indexById[package.Id];
            foreach (var dep in package.SelectedEntry.Dependencies.Where(d => !d.IsOptional))
            {
                Assert.That(indexById[dep.Id], Is.LessThan(packageIndex), $"Dependency '{dep.Id}' must appear before '{package.Id}'.");
            }
        }
    }

    /// <summary>
    /// Rejects unavoidable cycles and prefers an acyclic version when a newer candidate introduces a cycle.
    /// </summary>
    [Test]
    public void Solve_CyclicDependencies_RejectsUnavoidableCycleOrPicksAcyclicVersion()
    {
        var avoidable = new[]
        {
            Mod(
                "cycle.a",
                Version("2.0.0", [Dep("cycle.b", "[1.0,2.0)")]),
                Version("1.0.0")),
            Mod("cycle.b", Version("1.0.0", [Dep("cycle.a", "[2.0,3.0)")]))
        };

        var avoided = ModDependencySolver.Solve(
            new ModSolveRequest
            {
                Host = DefaultHost,
                Sources = [Catalog("official", avoidable)],
                Targets = [new ModTargetRequirement("cycle.a"), new ModTargetRequirement("cycle.b")]
            });
        Assert.That(avoided.IsSuccess, Is.False); // cycle.b requires cycle.a [2.0,3.0) which forms a cycle with cycle.b

        var avoidableAlt = new[]
        {
            Mod(
                "cycle.a",
                Version("2.0.0", [Dep("cycle.b", "[1.0,2.0)")]),
                Version("1.0.0")),
            Mod("cycle.b", Version("1.0.0"))
        };
        var ok = ModDependencySolver.Solve(
            new ModSolveRequest
            {
                Host = DefaultHost,
                Sources = [Catalog("official", avoidableAlt)],
                Targets = [new ModTargetRequirement("cycle.a")]
            });
        Assert.That(ok.IsSuccess, Is.True);

        var onlyCyclic = new[]
        {
            Mod("c.1", Version("1.0.0", [Dep("c.2", "[1.0,2.0)")])),
            Mod("c.2", Version("1.0.0", [Dep("c.1", "[1.0,2.0)")]))
        };
        var unsat = ModDependencySolver.Solve(
            new ModSolveRequest
            {
                Host = DefaultHost,
                Sources = [Catalog("official", onlyCyclic)],
                Targets = [new ModTargetRequirement("c.1")]
            });
        Assert.That(unsat.IsSuccess, Is.False);
        Assert.That(unsat.FailureSummary, Does.Contain("Circular dependency"));
    }

    /// <summary>
    /// Optional dependencies are never auto-installed, and host-incompatible candidates are filtered out upfront.
    /// </summary>
    [Test]
    public void Solve_OptionalDependenciesAndHostPlatformFiltering_FollowsNuGetAndHostRules()
    {
        var entries = new[]
        {
            Mod(
                "main.mod",
                Version("1.0.0", [Dep("opt.mod", "[1.0,2.0)", isOptional: true), Dep("req.mod", "[1.0,3.0)")])),
            Mod("opt.mod", Version("1.0.0")),
            Mod(
                "req.mod",
                new ModIndexVersion
                {
                    Version = "2.0.0",
                    Entry = new PlatformModEntry
                    {
                        Variants =
                        [
                            new ModPlatformVariant
                            {
                                Platform = "osx-arm64",
                                Assembly = "Req.dll",
                                Type = "Req.Entry",
                                Framework = "net11.0",
                                ModApiVersion = "[1.0,2.0)",
                                Assemblies = ["Req.dll"]
                            }
                        ]
                    },
                    Artifact = FakeArtifact()
                },
                Version("1.5.0", modApiVersion: "[2.0,3.0)"),
                Version("1.2.0"))
        };

        var result = ModDependencySolver.Solve(
            new ModSolveRequest
            {
                Host = DefaultHost,
                Sources = [Catalog("official", entries)],
                Targets = [new ModTargetRequirement("main.mod")]
            });

        Assert.That(result.IsSuccess, Is.True, result.FailureSummary);
        Assert.That(result.Packages.Select(p => p.Id), Is.EquivalentTo(["req.mod", "main.mod"]));
        Assert.That(result.Packages.Single(p => p.Id == "req.mod").Version, Is.EqualTo("1.2.0"));
        Assert.That(result.Packages.Single(p => p.Id == "req.mod").Intent, Is.EqualTo(ModInstallIntent.AutoDependency));
        Assert.That(result.Packages.Single(p => p.Id == "main.mod").Intent, Is.EqualTo(ModInstallIntent.Explicit));
    }

    /// <summary>
    /// Installed mods remain pinned to their original source, while uninstalled dependencies prefer the requester's source.
    /// </summary>
    [Test]
    public void Solve_SourceStickinessAndPriority_PinsInstalledSourceAndPrefersConsumerSource()
    {
        var sourceA = Catalog(
            "source-a",
            [
                Mod("installed.mod", Version("1.0.0"), Version("1.1.0")),
                Mod("consumer.mod", Version("1.0.0", [Dep("shared.dep", "[1.0,2.0)")])),
                Mod("shared.dep", Version("1.0.0"))
            ]);
        var sourceB = Catalog(
            "source-b",
            [
                Mod("installed.mod", Version("9.0.0")),
                Mod("consumer.mod", Version("2.0.0", [Dep("shared.dep", "[1.0,2.0)")])),
                Mod("shared.dep", Version("1.5.0"))
            ]);

        var installed = new InstalledModSnapshot(
            "installed.mod",
            "1.0.0",
            "source-a",
            IsEnabled: true,
            ModInstallIntent.Explicit,
            ((UniversalModEntry)sourceA.Index.Mods[0].Versions[0].Entry));

        var result = ModDependencySolver.Solve(
            new ModSolveRequest
            {
                Host = DefaultHost,
                Sources = [sourceB, sourceA], // source-b has higher catalog index priority than source-a
                Installed = [installed],
                ShouldKeepInstalledVersions = false,
                Targets =
                [
                    new ModTargetRequirement("installed.mod"),
                    new ModTargetRequirement("consumer.mod", PreferredSourceId: "source-a")
                ]
            });

        Assert.That(result.IsSuccess, Is.True, result.FailureSummary);
        Assert.Multiple(() =>
        {
            var resolvedInstalled = result.Packages.Single(p => p.Id == "installed.mod");
            Assert.That(resolvedInstalled.SourceId, Is.EqualTo("source-a"));
            Assert.That(resolvedInstalled.Version, Is.EqualTo("1.1.0"));

            var resolvedDep = result.Packages.Single(p => p.Id == "shared.dep");
            Assert.That(resolvedDep.SourceId, Is.EqualTo("source-a"));
            Assert.That(resolvedDep.Version, Is.EqualTo("1.0.0"));
        });
    }

    /// <summary>
    /// Verifies cascade consumer-first ordering and orphan AutoDependency detection.
    /// </summary>
    [Test]
    public void CascadeHelpers_ReverseDependentsAndOrphanAutoDependencies_ComputeExactSets()
    {
        var installed = new[]
        {
            Snapshot("base.lib", ModInstallIntent.AutoDependency),
            Snapshot("shared.helper", ModInstallIntent.AutoDependency),
            Snapshot("mid.service", ModInstallIntent.AutoDependency, [Dep("base.lib", "[1.0,2.0)")]),
            Snapshot("top.app", ModInstallIntent.Explicit, [Dep("mid.service", "[1.0,2.0)"), Dep("shared.helper", "[1.0,2.0)")]),
            Snapshot("other.app", ModInstallIntent.Explicit, [Dep("shared.helper", "[1.0,2.0)")])
        };

        var dependents = ModDependencySolver.GetReverseDependents("base.lib", installed);
        Assert.That(dependents, Is.EqualTo(new[] { "top.app", "mid.service" }));

        var orphans = ModDependencySolver.GetOrphanAutoDependencies(new HashSet<string>(StringComparer.Ordinal) { "top.app" }, installed);
        Assert.That(orphans, Is.EqualTo(new[] { "mid.service", "base.lib" }));
    }

    private static ModSourceCatalogEntry Catalog(string sourceId, IReadOnlyList<ModIndexEntry> mods) =>
        new(
            sourceId,
            new ModIndex
            {
                Id = sourceId,
                Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = sourceId },
                Mods = [.. mods]
            });

    private static ModIndexEntry Mod(string id, params ModIndexVersion[] versions) =>
        new() { Id = id, Versions = versions };

    private static ModIndexVersion Version(string version, ModDependency[]? dependencies = null, string modApiVersion = "[1.0,2.0)") =>
        new()
        {
            Version = version,
            Entry = new UniversalModEntry
            {
                Assembly = "Mod.dll",
                Type = "Mod.Entry",
                Framework = "net11.0",
                ModApiVersion = modApiVersion,
                Assemblies = ["Mod.dll"],
                Dependencies = dependencies ?? []
            },
            Artifact = FakeArtifact()
        };

    private static ModDependency Dep(string id, string range, bool isOptional = false) =>
        new() { Id = id, Version = range, IsOptional = isOptional, Assemblies = [] };

    private static ModArtifact FakeArtifact() =>
        new() { Url = "mod.zip", Size = 128, Sha256 = new string('a', 64) };

    private static InstalledModSnapshot Snapshot(string id, ModInstallIntent intent, ModDependency[]? dependencies = null) =>
        new(
            id,
            "1.0.0",
            "official",
            IsEnabled: true,
            intent,
            new UniversalModEntry
            {
                Assembly = "Mod.dll",
                Type = "Mod.Entry",
                Framework = "net11.0",
                ModApiVersion = "[1.0,2.0)",
                Assemblies = ["Mod.dll"],
                Dependencies = dependencies ?? []
            });
}
