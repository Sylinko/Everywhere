using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsDescriptorProviderFactory = Everywhere.Configuration.Engine.SettingsDescriptorProviderFactory;

namespace Everywhere.Core.Tests.Configuration;

public sealed class SettingsEngineTests
{
    [Test]
    public async Task InitializeAsync_PatchesExistingSettingsRoot()
    {
        using var file = TestSettingsFile(
            """
            {
              "Version": "99.0.0",
              "Common": {
                "IsStatisticsEnabled": false
              }
            }
            """);
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(serviceProvider);
        var engine = new SettingsEngine(settings, file.Path, serviceProvider, NullLoggerFactory.Instance);
        await engine.InitializeAsync();

        Assert.That(engine.Settings, Is.SameAs(settings));
        Assert.That(settings.Version, Is.EqualTo("99.0.0"));
        Assert.That(settings.Common.IsStatisticsEnabled, Is.False);
        Assert.That(engine.Diagnostics, Is.Empty);
    }

    [Test]
    public void AddSettings_RegistersRuntimeSettingsAndInitializerOrder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Pass("AddSettings registers Windows-only settings controls in Windows builds.");
            return;
        }

        using var serviceProvider = new ServiceCollection()
            .AddLogging()
            .AddSettings()
            .BuildServiceProvider();

        Assert.That(serviceProvider.GetRequiredService<Settings>(), Is.Not.Null);
        Assert.Throws<InvalidOperationException>(() => serviceProvider.GetRequiredService<SettingsEngine>());

        var initializers = serviceProvider.GetServices<IAsyncInitializer>().ToList();
        Assert.That(initializers.OfType<SettingsEngine>().Single().Index, Is.EqualTo(AsyncInitializerIndex.Settings));
        Assert.That(initializers.OfType<PersistentKeyValueStorage>().Single().Index, Is.EqualTo(AsyncInitializerIndex.Settings + 1));
    }

    [Test]
    public void DefaultDescriptorProvider_UsesGeneratedSettingsDescriptorProvider()
    {
        var provider = SettingsDescriptorProviderFactory.Create();

        var descriptor = provider.GetDescriptor(typeof(Settings));

        Assert.That(provider.GetType().Name, Is.EqualTo("GeneratedSettingsDescriptorProvider"));
        Assert.That(descriptor.FindProperty(nameof(Settings.Common)), Is.Not.Null);
    }

    [Test]
    public void GeneratedDescriptor_UsesSinglePropertyKindAndConstructorFactory()
    {
        var provider = SettingsDescriptorProviderFactory.Create();
        var descriptor = provider.GetDescriptor(typeof(Settings));

        var common = descriptor.FindProperty(nameof(Settings.Common));

        Assert.That(common, Is.Not.Null);
        if (common is null)
        {
            return;
        }

        Assert.That(common.Kind, Is.EqualTo(SettingsPropertyKind.Object));
        var childDescriptor = common.ChildDescriptor;
        Assert.That(childDescriptor, Is.Not.Null);
        if (childDescriptor is null)
        {
            return;
        }

        using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        Assert.That(childDescriptor.TryCreateInstance(serviceProvider, out var instance), Is.True);
        Assert.That(instance, Is.TypeOf<CommonSettings>());
        Assert.That(typeof(ISettingsPropertyDescriptor).GetProperty("CollectionBinding"), Is.Null);
    }

    [Test]
    public void Patch_PreservesExistingObjectReferences()
    {
        using var file = TestSettingsFile(
            """
            {
              "Section": {
                "Name": "from-json"
              },
              "GetterOnly": {
                "Name": "getter-json"
              }
            }
            """);
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        var section = target.Section;
        var getterOnly = target.GetterOnly;

        new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider()).Patch(store.CreateSnapshot(), target);

        Assert.That(target.Section, Is.SameAs(section));
        Assert.That(target.GetterOnly, Is.SameAs(getterOnly));
        Assert.That(target.Section.Name, Is.EqualTo("from-json"));
        Assert.That(target.GetterOnly.Name, Is.EqualTo("getter-json"));
    }

    [Test]
    public void Patch_ListPatchesExistingItemsByIndexAndRemovesTail()
    {
        using var file = TestSettingsFile(
            """
            {
              "Items": [
                { "Name": "one" },
                { "Name": "two" }
              ]
            }
            """);
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        var first = new TestItem { Name = "old-one" };
        var second = new TestItem { Name = "old-two" };
        var removed = new TestItem { Name = "old-three" };
        target.Items.Add(first);
        target.Items.Add(second);
        target.Items.Add(removed);
        var items = target.Items;

        new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider()).Patch(store.CreateSnapshot(), target);

        Assert.That(target.Items, Is.SameAs(items));
        Assert.That(target.Items, Has.Count.EqualTo(2));
        Assert.That(target.Items[0], Is.SameAs(first));
        Assert.That(target.Items[1], Is.SameAs(second));
        Assert.That(target.Items.Select(i => i.Name), Is.EqualTo(new[] { "one", "two" }));
    }

    [Test]
    public void Patch_ListAppendsNewObjectItems()
    {
        using var file = TestSettingsFile(
            """
            {
              "Items": [
                { "Name": "one" },
                { "Name": "two" }
              ]
            }
            """);
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        var first = new TestItem { Name = "old" };
        target.Items.Add(first);
        var items = target.Items;

        new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider()).Patch(store.CreateSnapshot(), target);

        Assert.That(target.Items, Is.SameAs(items));
        Assert.That(target.Items, Has.Count.EqualTo(2));
        Assert.That(target.Items[0], Is.SameAs(first));
        Assert.That(target.Items[0].Name, Is.EqualTo("one"));
        Assert.That(target.Items[1].Name, Is.EqualTo("two"));
    }

    [Test]
    public void Patch_ScalarListReplacesItemsByIndexAndRemovesTail()
    {
        using var file = TestSettingsFile("""{ "Numbers": [1, 2] }""");
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        target.Numbers.Add(9);
        target.Numbers.Add(8);
        target.Numbers.Add(7);
        var numbers = target.Numbers;

        new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider()).Patch(store.CreateSnapshot(), target);

        Assert.That(target.Numbers, Is.SameAs(numbers));
        Assert.That(target.Numbers.ToArray(), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void Patch_DictionaryPatchesExistingObjectValuesAndAddsAndRemovesKeys()
    {
        using var file = TestSettingsFile(
            """
            {
              "ItemMap": {
                "keep": { "Name": "new" },
                "add": { "Name": "added" }
              }
            }
            """);
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        var kept = new TestItem { Name = "old" };
        target.ItemMap["keep"] = kept;
        target.ItemMap["remove"] = new TestItem { Name = "remove" };
        var itemMap = target.ItemMap;

        new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider()).Patch(store.CreateSnapshot(), target);

        Assert.That(target.ItemMap, Is.SameAs(itemMap));
        Assert.That(target.ItemMap.Keys, Is.EquivalentTo(new[] { "keep", "add" }));
        Assert.That(target.ItemMap["keep"], Is.SameAs(kept));
        Assert.That(target.ItemMap["keep"].Name, Is.EqualTo("new"));
        Assert.That(target.ItemMap["add"].Name, Is.EqualTo("added"));
    }

    [Test]
    public void Patch_DictionaryValueConversionFailureKeepsOldValue()
    {
        using var file = TestSettingsFile("""{ "Scores": { "bad": "not-int", "good": 7 } }""");
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        target.Scores["bad"] = 5;
        target.Scores["remove"] = 9;
        var scores = target.Scores;
        var binder = new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider());

        binder.Patch(store.CreateSnapshot(), target);

        Assert.That(target.Scores, Is.SameAs(scores));
        Assert.That(target.Scores["bad"], Is.EqualTo(5));
        Assert.That(target.Scores["good"], Is.EqualTo(7));
        Assert.That(target.Scores.ContainsKey("remove"), Is.False);
        Assert.That(binder.Diagnostics.Any(d => d.Kind == SettingsEngineDiagnosticKind.ScalarConversionFailure), Is.True);
    }

    [Test]
    public void Patch_ReadOnlyDictionaryOnlyPatchesExistingObjectValues()
    {
        using var file = TestSettingsFile(
            """
            {
              "ReadOnlyItems": {
                "existing": { "Name": "new" },
                "new": { "Name": "added" }
              }
            }
            """);
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        var readOnlyItems = target.ReadOnlyItems;
        var existing = target.ReadOnlyItems["existing"];

        new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider()).Patch(store.CreateSnapshot(), target);

        Assert.That(target.ReadOnlyItems, Is.SameAs(readOnlyItems));
        Assert.That(target.ReadOnlyItems["existing"], Is.SameAs(existing));
        Assert.That(target.ReadOnlyItems["existing"].Name, Is.EqualTo("new"));
        Assert.That(target.ReadOnlyItems.ContainsKey("new"), Is.False);
    }

    [Test]
    public void Patch_WritableReadOnlyListFallsBackToWholePropertyReplacement()
    {
        using var file = TestSettingsFile("""{ "WritableReadOnlyNumbers": [1, 2] }""");
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        var original = target.WritableReadOnlyNumbers;

        new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider()).Patch(store.CreateSnapshot(), target);

        Assert.That(target.WritableReadOnlyNumbers, Is.Not.SameAs(original));
        Assert.That(target.WritableReadOnlyNumbers.ToArray(), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void Patch_GetterOnlyReadOnlyListReportsDiagnostic()
    {
        using var file = TestSettingsFile("""{ "GetterOnlyReadOnlyNumbers": [1, 2] }""");
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        var original = target.GetterOnlyReadOnlyNumbers;
        var binder = new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider());

        binder.Patch(store.CreateSnapshot(), target);

        Assert.That(target.GetterOnlyReadOnlyNumbers, Is.SameAs(original));
        Assert.That(target.GetterOnlyReadOnlyNumbers.ToArray(), Is.EqualTo(new[] { 9 }));
        Assert.That(binder.Diagnostics.Any(d => d.Kind == SettingsEngineDiagnosticKind.UnsupportedShape), Is.True);
    }

    [Test]
    public void Patch_PrunesUnknownKeysWhenPolicyRequestsIt()
    {
        using var file = TestSettingsFile(
            """
            {
              "Strict": {
                "Name": "known",
                "Unknown": true
              }
            }
            """);
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        var root = store.CreateSnapshot();

        new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider()).Patch(root, target);

        var strict = Require(root["Strict"]).AsObject();
        Assert.That(target.Strict.Name, Is.EqualTo("known"));
        Assert.That(strict.ContainsKey("Unknown"), Is.False);
    }

    [Test]
    public void Patch_SerializedSubtreeFailureKeepsExistingValue()
    {
        using var file = TestSettingsFile("""{ "Serialized": "bad-shape" }""");
        using var store = JsonSettingsStorage.Load(file.Path);

        var target = new TestRoot();
        var original = target.Serialized;
        var binder = new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider());

        binder.Patch(store.CreateSnapshot(), target);

        Assert.That(target.Serialized, Is.SameAs(original));
        Assert.That(target.Serialized.Value, Is.EqualTo(42));
        Assert.That(binder.Diagnostics.Any(d => d.Kind == SettingsEngineDiagnosticKind.SerializedSubtreeFailure), Is.True);
    }

    [Test]
    public void WriteObservedPath_PreservesUnknownSiblingKeys()
    {
        using var file = TestSettingsFile(
            """
            {
              "Section": {
                "Name": "old",
                "Unknown": true
              }
            }
            """);
        using var store = JsonSettingsStorage.Load(file.Path);
        var binder = new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider());
        var descriptor = binder.GetDescriptor(typeof(TestRoot));

        binder.WriteObservedPath(store, descriptor, "Section:Name", "new");

        var section = Require(store.CreateSnapshot()["Section"]).AsObject();
        Assert.That(Require(section["Name"]).GetValue<string>(), Is.EqualTo("new"));
        Assert.That(Require(section["Unknown"]).GetValue<bool>(), Is.True);
    }

    [Test]
    public void WriteObservedPath_HonorsJsonPropertyName()
    {
        using var file = TestSettingsFile("""{ "Renamed": { "json_name": "old" } }""");
        using var store = JsonSettingsStorage.Load(file.Path);
        var binder = new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider());
        var descriptor = binder.GetDescriptor(typeof(TestRoot));

        binder.WriteObservedPath(store, descriptor, "Renamed:Name", "new");

        var renamed = Require(store.CreateSnapshot()["Renamed"]).AsObject();
        Assert.That(Require(renamed["json_name"]).GetValue<string>(), Is.EqualTo("new"));
        Assert.That(renamed.ContainsKey("Name"), Is.False);
    }

    [Test]
    public void WriteObservedPath_WritesRuntimeValuesAsJsonTypes()
    {
        using var file = TestSettingsFile("{}");
        using var store = JsonSettingsStorage.Load(file.Path);
        var binder = new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider());
        var descriptor = binder.GetDescriptor(typeof(TestRoot));

        binder.WriteObservedPath(store, descriptor, "Count", 3);

        var count = Require(store.CreateSnapshot()["Count"]);
        Assert.That(count.GetValueKind(), Is.EqualTo(JsonValueKind.Number));
        Assert.That(count.GetValue<int>(), Is.EqualTo(3));
    }

    [Test]
    public void WriteObservedPath_TreatsDictionaryNumericKeysAsObjectProperties()
    {
        using var file = TestSettingsFile("{}");
        using var store = JsonSettingsStorage.Load(file.Path);
        var binder = new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider());
        var descriptor = binder.GetDescriptor(typeof(TestRoot));

        binder.WriteObservedPath(store, descriptor, "Scores:0", 9);

        var scores = Require(store.CreateSnapshot()["Scores"]).AsObject();
        Assert.That(Require(scores["0"]).GetValueKind(), Is.EqualTo(JsonValueKind.Number));
        Assert.That(Require(scores["0"]).GetValue<int>(), Is.EqualTo(9));
    }

    [Test]
    public void WriteObservedPath_TreatsCollectionIndexesAsArrayIndexes()
    {
        using var file = TestSettingsFile("""{ "Items": [ { "Name": "old" } ] }""");
        using var store = JsonSettingsStorage.Load(file.Path);
        var binder = new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider());
        var descriptor = binder.GetDescriptor(typeof(TestRoot));

        binder.WriteObservedPath(store, descriptor, "Items:0:Name", "new");

        var items = Require(store.CreateSnapshot()["Items"]).AsArray();
        Assert.That(Require(Require(items[0])["Name"]).GetValue<string>(), Is.EqualTo("new"));
    }

    [Test]
    public void Store_DistinguishesNumericObjectPropertyFromArrayIndex()
    {
        using var file = TestSettingsFile("{}");
        using var store = JsonSettingsStorage.Load(file.Path);

        store.ReplaceSubtree(
            new SettingsJsonPath([SettingsJsonPathSegment.Property("0")]),
            JsonValue.Create("property"));
        store.ReplaceSubtree(
            new SettingsJsonPath([SettingsJsonPathSegment.Property("Array"), SettingsJsonPathSegment.Index(0)]),
            JsonValue.Create("array"));

        var root = store.CreateSnapshot();
        Assert.That(Require(root["0"]).GetValue<string>(), Is.EqualTo("property"));
        Assert.That(Require(Require(root["Array"]).AsArray()[0]).GetValue<string>(), Is.EqualTo("array"));
    }

    [Test]
    public async Task Store_FlushAsyncWritesPendingChanges()
    {
        using var file = TestSettingsFile("{}");
        using var store = JsonSettingsStorage.Load(file.Path);

        store.ReplaceSubtree(
            new SettingsJsonPath([SettingsJsonPathSegment.Property("Count")]),
            JsonValue.Create(5));
        await store.FlushAsync();

        var root = Require(JsonNode.Parse(File.ReadAllText(file.Path))).AsObject();
        Assert.That(Require(root["Count"]).GetValue<int>(), Is.EqualTo(5));
    }

    [Test]
    public async Task Store_DebouncedSaveEventuallyWritesChanges()
    {
        using var file = TestSettingsFile("{}");
        using var store = JsonSettingsStorage.Load(file.Path);

        store.ReplaceSubtree(
            new SettingsJsonPath([SettingsJsonPathSegment.Property("Name")]),
            JsonValue.Create("debounced"));

        await Task.Delay(800);

        var root = Require(JsonNode.Parse(File.ReadAllText(file.Path))).AsObject();
        Assert.That(Require(root["Name"]).GetValue<string>(), Is.EqualTo("debounced"));
    }

    [Test]
    public async Task Store_ConcurrentWritesProduceValidJson()
    {
        using var file = TestSettingsFile("{}");
        using var store = JsonSettingsStorage.Load(file.Path);

        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            store.ReplaceSubtree(
                new SettingsJsonPath([SettingsJsonPathSegment.Property("Values"), SettingsJsonPathSegment.Property(i.ToString())]),
                JsonValue.Create(i))));

        await Task.WhenAll(tasks);
        await store.FlushAsync();

        var values = Require(Require(JsonNode.Parse(await File.ReadAllTextAsync(file.Path)))["Values"]).AsObject();
        Assert.That(values, Has.Count.EqualTo(40));
        Assert.That(Require(values["39"]).GetValue<int>(), Is.EqualTo(39));
    }

    [Test]
    public async Task Store_WriteFailureAddsDiagnosticAndDeletesTemporaryFiles()
    {
        using var directory = new TempDirectory();
        var invalidTarget = Path.Combine(directory.Path, "settings-directory");
        Directory.CreateDirectory(invalidTarget);
        using var store = JsonSettingsStorage.Load(invalidTarget);

        store.ReplaceSubtree(
            new SettingsJsonPath([SettingsJsonPathSegment.Property("Name")]),
            JsonValue.Create("cannot-write-over-directory"));
        await store.FlushAsync();

        Assert.That(store.Diagnostics.Any(d => d.Kind == SettingsEngineDiagnosticKind.WriteFailure), Is.True);
        Assert.That(Directory.GetFiles(directory.Path), Is.Empty);
    }

    private static TempSettingsFile TestSettingsFile(string json) => new(json);

    private static JsonNode Require(JsonNode? node) =>
        node ?? throw new AssertionException("Expected JSON node to exist.");

    private sealed class TestRoot
    {
        public TestSection Section { get; set; } = new();
        public TestSection GetterOnly { get; } = new();
        public ObservableCollection<TestItem> Items { get; } = [];
        public ObservableCollection<int> Numbers { get; } = [];
        public Dictionary<string, int> Scores { get; } = [];
        public Dictionary<string, TestItem> ItemMap { get; } = [];
        public ObservableImmutableDictionary<string, TestItem> ReadOnlyItems { get; } = new(
        [
            new KeyValuePair<string, TestItem>("existing", new TestItem { Name = "old" })
        ]);
        public IReadOnlyList<int> WritableReadOnlyNumbers { get; set; } = new ReadOnlyCollection<int>(new List<int> { 9 });
        public IReadOnlyList<int> GetterOnlyReadOnlyNumbers { get; } = new ReadOnlyCollection<int>(new List<int> { 9 });
        public int Count { get; set; }
        public SerializableColor Color { get; set; }

        [SettingsUnknownMemberHandling(SettingsUnknownMemberHandling.Prune)]
        public TestSection Strict { get; set; } = new();

        public RenamedSection Renamed { get; set; } = new();

        [SettingsSerializedSubtree]
        public SerializedThing Serialized { get; set; } = new() { Value = 42 };
    }

    private sealed class TestSection
    {
        public string? Name { get; set; }
    }

    private sealed class RenamedSection
    {
        [JsonPropertyName("json_name")]
        public string? Name { get; set; }
    }

    private sealed class TestItem
    {
        public string? Name { get; set; }
    }

    private sealed class SerializedThing
    {
        public int Value { get; set; }
    }

    private sealed class TempSettingsFile : IDisposable
    {
        private readonly TempDirectory _directory;

        public TempSettingsFile(string json)
        {
            _directory = new TempDirectory();
            Path = System.IO.Path.Combine(_directory.Path, "settings.json");
            File.WriteAllText(Path, json);
        }

        public string Path { get; }

        public void Dispose() => _directory.Dispose();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Everywhere.Core.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
