using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Hosting.Services;
using Everywhere.Modding.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Verifies collectible context matching across compound types and delegates,
/// and Avalonia dispatcher and sweeper cleanup during mod retirement.
/// </summary>
[NonParallelizable]
public sealed class ModContextCleanupTests
{
    private static readonly string[] ExpectedCleanupOrder =
    [
        "entry-dispose:fixture.a",
        "dispose:second:fixture.a",
        "dispose:first:fixture.a",
        "sweep-ui",
        "sweep-nonui"
    ];

    private static readonly Type MatcherType = typeof(ModEngine).Assembly.GetType(
        "Everywhere.Modding.Hosting.Services.CollectibleContextMatcher",
        throwOnError: true)
        ?? throw new InvalidOperationException("Missing CollectibleContextMatcher type.");

    private static readonly MethodInfo ReferencesTypeMethod = MatcherType.GetMethod(
        "References",
        BindingFlags.Static | BindingFlags.NonPublic,
        [typeof(Type), typeof(IReadOnlySet<AssemblyLoadContext>)])
        ?? throw new InvalidOperationException("Missing CollectibleContextMatcher.References(Type, IReadOnlySet<AssemblyLoadContext>).");

    private static readonly MethodInfo ReferencesDelegateMethod = MatcherType.GetMethod(
        "References",
        BindingFlags.Static | BindingFlags.NonPublic,
        [typeof(Delegate), typeof(IReadOnlySet<AssemblyLoadContext>)])
        ?? throw new InvalidOperationException("Missing CollectibleContextMatcher.References(Delegate, IReadOnlySet<AssemblyLoadContext>).");

    private static readonly MethodInfo ReferencesObjectMethod = MatcherType.GetMethod(
        "ReferencesObject",
        BindingFlags.Static | BindingFlags.NonPublic,
        [typeof(object), typeof(IReadOnlySet<AssemblyLoadContext>)])
        ?? throw new InvalidOperationException("Missing CollectibleContextMatcher.ReferencesObject.");

    private string _dataDirectory = "";
    private readonly List<HostServiceContainer> _containers = [];

    /// <summary>
    /// Allocates temporary data directories for engine integration tests.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "Everywhere.Modding.Tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Disposes test service containers and deletes temporary directories.
    /// </summary>
    [TearDown]
    public async Task TearDown()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }

        _containers.Clear();
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }

    /// <summary>
    /// Compound generics, arrays, open generic definitions, static-method delegates,
    /// constructed generic host methods, and multicast invocation lists must match every referenced ALC.
    /// </summary>
    [Test]
    public void References_CrossAlcCompoundTypesAndDelegates_MatchAllReferencedContexts()
    {
        var alcA = LoadFixtureContext("ModFixture.A.dll");
        var alcB = LoadFixtureContext("ModFixture.B.dll");
        var alcUnrelated = LoadFixtureContext("ModFixture.B.dll");
        try
        {
            var typeA = alcA.LoadedAssembly.GetType("ModFixture.Entry") ?? throw new AssertionException("Missing ModFixture.Entry.");
            var typeB = alcB.LoadedAssembly.GetType("ModFixture.DependencyBase") ?? throw new AssertionException("Missing ModFixture.DependencyBase.");

            var compoundType = typeof(Dictionary<,>).MakeGenericType(typeA, typeB);
            var arrayType = typeof(List<>).MakeGenericType(typeA.MakeArrayType());
            var openNestedType = typeof(HostGenericOuter<>.Inner<>);
            var nestedWithModOuter = openNestedType.MakeGenericType(typeA, typeof(HostMarker));

            var staticModMethod = typeA.Assembly
                .GetType("ModFixture.NativeProbe")?
                .GetMethod("Query", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new AssertionException("Missing ModFixture.NativeProbe.Query.");
            var staticDelegate = Delegate.CreateDelegate(typeof(Func<uint>), staticModMethod);

            var hostGenericMethod = (typeof(HostHelper)
                .GetMethod(nameof(HostHelper.DescribeType), BindingFlags.Public | BindingFlags.Static)
                ?? throw new AssertionException("Missing HostHelper.DescribeType."))
                .MakeGenericMethod(typeA);
            var hostGenericDelegate = Delegate.CreateDelegate(typeof(Func<string>), hostGenericMethod);

            var multicast = Delegate.Combine((Func<uint>)HostHelper.Zero, staticDelegate);
            var instanceB = Activator.CreateInstance(typeB) ?? throw new AssertionException("Failed to activate ModFixture.DependencyBase.");
            var getValueMethod = typeB.GetProperty("Value")?.GetMethod ?? throw new AssertionException("Missing DependencyBase.Value getter.");
            var instanceDelegate = Delegate.CreateDelegate(typeof(Func<int>), instanceB, getValueMethod);
            var hostPlusInstanceMulticast = Delegate.Combine((Func<int>)HostHelper.HostInt, instanceDelegate);

            var setOnlyA = new HashSet<AssemblyLoadContext> { alcA };
            var setOnlyB = new HashSet<AssemblyLoadContext> { alcB };
            var setUnrelated = new HashSet<AssemblyLoadContext> { alcUnrelated };

            using (Assert.EnterMultipleScope())
            {
                Assert.That(References(typeof(string), setOnlyA), Is.False);
                Assert.That(References(typeof(Dictionary<string, int>), setOnlyA), Is.False);

                Assert.That(References(compoundType, setOnlyA), Is.True);
                Assert.That(References(compoundType, setOnlyB), Is.True);
                Assert.That(References(compoundType, setUnrelated), Is.False);

                Assert.That(References(arrayType, setOnlyA), Is.True);
                Assert.That(References(openNestedType, setOnlyA), Is.False);
                Assert.That(References(nestedWithModOuter, setOnlyA), Is.True);

                Assert.That(staticDelegate.GetType().IsCollectible, Is.False);
                Assert.That(staticDelegate.Target, Is.Null);
                Assert.That(References(staticDelegate, setOnlyA), Is.True);
                Assert.That(References(staticDelegate, setOnlyB), Is.False);

                Assert.That(hostGenericDelegate.Target, Is.Null);
                Assert.That(References(hostGenericDelegate, setOnlyA), Is.True);
                Assert.That(References(hostGenericDelegate, setOnlyB), Is.False);

                Assert.That(References(multicast, setOnlyA), Is.True);
                Assert.That(References(multicast, setOnlyB), Is.False);
                Assert.That(References(hostPlusInstanceMulticast, setOnlyB), Is.True);
                Assert.That(References(hostPlusInstanceMulticast, setOnlyA), Is.False);
                Assert.That(ReferencesObject(compoundType, setOnlyB), Is.True);
                Assert.That(ReferencesObject(staticDelegate, setOnlyA), Is.True);
                Assert.That(ReferencesObject(instanceB, setOnlyB), Is.True);
            }
        }
        finally
        {
            alcA.Release();
            alcB.Release();
            alcUnrelated.Release();
        }
    }

    /// <summary>
    /// Unloading through <see cref="ModEngine"/> drains the Avalonia UI dispatcher and invokes UI and non-UI sweepers
    /// after mod disposal, enabling host-held Type/Delegate caches to release cleanly.
    /// </summary>
    [AvaloniaTest]
    public async Task ModEngine_ApplyAsync_SweepsAvaloniaAndNonUiCachesAfterDisposal()
    {
        var hostTypeCache = new List<Type>();
        var hostDelegateCache = new List<Delegate>();
        var events = new List<string>();
        using var uiSweeper = CollectibleAssemblyCleanup.RegisterSweeper(
            contexts =>
            {
                Assert.That(Dispatcher.UIThread.CheckAccess(), Is.True);
                events.Add("sweep-ui");
                hostTypeCache.RemoveAll(type => References(type, contexts));
            },
            isUIThreadRequired: true);
        using var nonUiSweeper = CollectibleAssemblyCleanup.RegisterSweeper(
            contexts =>
            {
                events.Add("sweep-nonui");
                hostDelegateCache.RemoveAll(handler => References(handler, contexts));
            },
            isUIThreadRequired: false);

        var options = new ModEngineOptions
        {
            CallbackTimeout = TimeSpan.FromSeconds(2),
            CollectionAttempts = 12,
            CollectionDelay = TimeSpan.FromMilliseconds(10),
        };
        var engine = CreateFixtureAEngine(
            events.Add,
            (_, entryType) =>
            {
                hostTypeCache.Add(entryType);
                var staticMethod = entryType.Assembly
                    .GetType("ModFixture.NativeProbe")?
                    .GetMethod("Query", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new AssertionException("Missing ModFixture.NativeProbe.Query.");
                hostDelegateCache.Add(Delegate.CreateDelegate(typeof(Func<uint>), staticMethod));
            },
            options);
        var definition = CreateFixtureADefinition();

        var started = await engine.ApplyAsync([definition]);
        Assert.That(
            started.Status,
            Is.EqualTo(ModApplyStatus.Applied),
            string.Join(Environment.NewLine, started.Diagnostics.Select(static d => $"{d.Code}: {d.Detail}")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hostTypeCache, Has.Count.EqualTo(1));
            Assert.That(hostDelegateCache, Has.Count.EqualTo(1));
        }

        events.Clear();
        var stopped = await engine.ApplyAsync([]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stopped.Status, Is.EqualTo(ModApplyStatus.Applied));
            Assert.That(stopped.ResidentAssemblyNames, Is.Empty);
            Assert.That(hostTypeCache, Is.Empty);
            Assert.That(hostDelegateCache, Is.Empty);
            Assert.That(events, Is.EqualTo(ExpectedCleanupOrder));
        }
    }

    /// <summary>
    /// When an assembly context stays alive past the initial sweep and first GC pass, retry iterations
    /// must re-drain the Avalonia UI dispatcher and re-run sweepers until the context collects.
    /// </summary>
    [AvaloniaTest]
    public async Task ModEngine_ApplyAsync_WhenInitialGcLeavesContextAlive_ResweepsDispatcherDuringRetryLoop()
    {
        var hostTypeCache = new List<Type>();
        var uiSweepCount = 0;
        var nonUiSweepCount = 0;
        using var uiSweeper = CollectibleAssemblyCleanup.RegisterSweeper(
            contexts =>
            {
                Assert.That(Dispatcher.UIThread.CheckAccess(), Is.True);
                uiSweepCount++;
                if (uiSweepCount >= 2)
                {
                    hostTypeCache.RemoveAll(type => References(type, contexts));
                }
            },
            isUIThreadRequired: true);
        using var nonUiSweeper = CollectibleAssemblyCleanup.RegisterSweeper(
            _ => nonUiSweepCount++,
            isUIThreadRequired: false);

        var engine = CreateFixtureAEngine(
            _ => { },
            (_, entryType) => hostTypeCache.Add(entryType),
            new ModEngineOptions
            {
                CallbackTimeout = TimeSpan.FromSeconds(2),
                CollectionAttempts = 8,
                CollectionDelay = TimeSpan.FromMilliseconds(10),
            });
        var definition = CreateFixtureADefinition();

        var started = await engine.ApplyAsync([definition]);
        Assert.That(started.Status, Is.EqualTo(ModApplyStatus.Applied));
        Assert.That(hostTypeCache, Has.Count.EqualTo(1));

        var stopped = await engine.ApplyAsync([]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stopped.Status, Is.EqualTo(ModApplyStatus.Applied));
            Assert.That(stopped.ResidentAssemblyNames, Is.Empty);
            Assert.That(hostTypeCache, Is.Empty);
            Assert.That(uiSweepCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(nonUiSweepCount, Is.GreaterThanOrEqualTo(2));
        }
    }

    private ModEngine CreateFixtureAEngine(
        Action<string> onEvent,
        Action<IServiceProvider, Type> onEntryType,
        ModEngineOptions options)
    {
        var registrations = new ServiceCollection();
        registrations.AddSingleton<Action<string>>(_ => onEvent);
        registrations.AddTransient(_ => "normal");
        registrations.AddSingleton(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        registrations.AddSingleton<Action<IServiceProvider, Type>>(_ => onEntryType);
        registrations.AddSingleton<Action<object>>(_ => _ => { });

        var container = new HostServiceContainer(registrations);
        _containers.Add(container);

        return new ModEngine(
            new HostAssemblyCatalog([typeof(ILogger<>).Assembly.Location]),
            container,
            options);
    }

    private ModDefinition CreateFixtureADefinition() =>
        new(
            new ModInstallation
            {
                InstallationId = "A",
                Directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures"),
                Manifest = new ModManifest
                {
                    Id = "fixture.a",
                    Version = "1.0.0",
                    Entry = new UniversalModEntry
                    {
                        Assembly = "ModFixture.A.dll",
                        Type = "ModFixture.Entry",
                        Framework = "net11.0",
                        ModApiVersion = "[1.0,2.0)",
                        Assemblies = ["ModFixture.A.dll"],
                        Dependencies =
                        [
                            new ModDependency
                            {
                                Id = "fixture.b",
                                Version = "[1.0,2.0)",
                                IsOptional = true,
                                Assemblies = ["ModFixture.B"]
                            }
                        ]
                    }
                }
            },
            Path.Combine(_dataDirectory, "fixture.a"),
            "win-x64",
            "1.0.0",
            "1.0.0",
            "net11.0");

    private static bool References(Type? type, IReadOnlySet<AssemblyLoadContext> contexts) =>
        (bool)ReferencesTypeMethod.Invoke(null, [type, contexts])!;

    private static bool References(Delegate? handler, IReadOnlySet<AssemblyLoadContext> contexts) =>
        (bool)ReferencesDelegateMethod.Invoke(null, [handler, contexts])!;

    private static bool ReferencesObject(object? value, IReadOnlySet<AssemblyLoadContext> contexts) =>
        (bool)ReferencesObjectMethod.Invoke(null, [value, contexts])!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixtureContext LoadFixtureContext(string fileName)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", fileName);
        return new FixtureContext(path);
    }

    private sealed class FixtureContext : AssemblyLoadContext
    {
        internal Assembly LoadedAssembly => _assembly ?? throw new InvalidOperationException("Fixture assembly has been released.");

        private Assembly? _assembly;

        internal FixtureContext(string path) : base(Path.GetFileNameWithoutExtension(path), isCollectible: true)
        {
            using var stream = File.OpenRead(path);
            _assembly = LoadFromStream(stream);
        }

        internal void Release()
        {
            _assembly = null;
            Unload();
        }
    }

    private static class HostHelper
    {
        public static uint Zero() => 0;

        public static int HostInt() => 1;

        public static string DescribeType<T>() => typeof(T).FullName ?? string.Empty;
    }

    // ReSharper disable UnusedTypeParameter
    private static class HostGenericOuter<TOuter>
    {
        internal sealed class Inner<TInner>;
    }
    // ReSharper restore UnusedTypeParameter

    private sealed class HostMarker;
}
