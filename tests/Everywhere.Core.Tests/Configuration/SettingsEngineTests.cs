using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsDescriptorProviderFactory = Everywhere.Configuration.Engine.SettingsDescriptorProviderFactory;

namespace Everywhere.Core.Tests.Configuration;

public sealed class SettingsEngineTests
{
    [Test]
    public void Load_EmptyDocumentInitializesRealSettingsRoot()
    {
        using var file = TestSettingsFile("{}");
        using var engine = SettingsEngine.Load(file.Path, new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance);

        Assert.That(engine.Settings, Is.Not.Null);
        Assert.That(engine.Diagnostics, Is.Empty);
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
    public void Patch_ReplacesCollectionItemsWhileKeepingCollectionInstance()
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
        target.Items.Add(new TestItem { Name = "old" });
        var items = target.Items;

        new SettingsPatchBinder(new ServiceCollection().BuildServiceProvider()).Patch(store.CreateSnapshot(), target);

        Assert.That(target.Items, Is.SameAs(items));
        Assert.That(target.Items.Select(i => i.Name), Is.EqualTo(new[] { "one", "two" }));
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
        public Dictionary<string, int> Scores { get; } = [];
        public int Count { get; set; }

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
