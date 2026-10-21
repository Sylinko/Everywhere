using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.I18N;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.Modding.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Core.Tests.Modding.Fixtures;

/// <summary>
/// Runs the production settings channel from a collectible activation, with generated metadata and mod key types.
/// </summary>
public sealed class CollectibleSettingsMod : IMod, IAsyncDisposable
{
    private IModContext? _context;
    private SettingsEngine<CollectibleSettings>? _engine;
    private IAsyncDisposable? _page;
    private IList<string>? _trace;

    /// <inheritdoc />
    public async ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _trace = context.Services.GetRequiredService<IList<string>>();
        var settings = new CollectibleSettings();
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ModIntConverter(context.ModId));
        var json = new FixtureSettingsJson(options);
        // Exercise the weak cache as well as the direct generated key reader.
        new ReflectionSettingsDescriptorProvider().GetDescriptor(typeof(CollectibleSettings));
        _engine = await context.OpenSettingsAsync(
            settings, new SemanticVersion(2), json.Options, new FixtureSettingsDescriptors(),
            configureMigrations: services => services.AddSingleton(_trace).AddSettingsMigration<UpgradeSettings>(),
            cancellationToken: cancellationToken);
        _trace.Add("opened:" + context.ModId);
        var registry = context.Services.GetRequiredService<IModSettingsRegistry>();
        _page = registry.Register(context, () =>
        {
            _trace.Add("view:" + context.ModId);
            return settings.SettingsItems;
        });
        context.Register(_page);
        context.Services.GetRequiredService<ICollection<IAsyncDisposable>>().Add(_page);
        settings.Count++;
        if (File.Exists(Path.Combine(context.DataDirectory, "fail-initialize")))
        {
            throw new InvalidOperationException("Initialization failed after settings migration.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_page is not null) await _page.DisposeAsync();
        if (_engine is not null)
        {
            _engine.Settings.Count++;
            await _engine.DisposeAsync();
        }

        _trace?.Add("disposed:" + _context?.StopReason);
    }
}

/// <summary>
/// Settings with notifications and a mod-defined dictionary key, independent of host Settings.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class CollectibleSettings : ObservableObject
{
    /// <summary>
    /// Gets or sets a value updated before final disposal.
    /// </summary>
    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    /// <summary>
    /// Gets or sets a UI-editable preference.
    /// </summary>
    [DynamicLocaleKey("SettingsFixture_IsEnabled", "SettingsFixture_IsEnabledDescription")]
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>
    /// Gets or sets values keyed by a collectible enum.
    /// </summary>
    public Dictionary<SettingsKey, int> Limits { get; set; } = new() { [SettingsKey.Search] = 7 };

    /// <summary>
    /// Exercises a permanently live key type with an activation-specific converter.
    /// </summary>
    public Dictionary<int, int> HostKeys { get; set; } = new() { [1] = 4 };

    private int _count;
    private bool _isEnabled;
}

/// <summary>
/// Provides a collectible key type for serialization and weak-cache verification.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsKey>))]
public enum SettingsKey
{
    /// <summary>
    /// Identifies the search preference.
    /// </summary>
    Search
}

/// <summary>
/// Provides the source-generated settings graph for this mod.
/// </summary>
[GeneratedSettingsDescriptor(typeof(CollectibleSettings))]
public partial class FixtureSettingsDescriptors;

/// <summary>
/// Keeps the serializer contract inside the collectible owner.
/// </summary>
[JsonSerializable(typeof(CollectibleSettings))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(SettingsKey))]
internal partial class FixtureSettingsJson : JsonSerializerContext;

/// <summary>
/// Records asynchronous migration cleanup before the document can be committed.
/// </summary>
public sealed class UpgradeSettings(IList<string> trace) : SettingsMigration, ISettingsMigrationMetadata, IAsyncDisposable
{
    /// <inheritdoc />
    public static SemanticVersion Version { get; } = new(2);

    /// <inheritdoc />
    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [Apply];

    /// <summary>
    /// Changes only JSON, before the runtime model is published.
    /// </summary>
    private bool Apply(JsonObject root)
    {
        trace.Add("migrate");
        root["Count"] = 10;
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        trace.Add("migration-disposed");
    }
}

/// <summary>
/// Makes cross-activation option reuse visible while retaining ordinary integer value serialization.
/// </summary>
internal sealed class ModIntConverter(string prefix) : JsonConverter<int>
{
    /// <inheritdoc />
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetInt32();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);

    /// <inheritdoc />
    public override int ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var key = reader.GetString() ?? throw new JsonException("Missing dictionary key.");
        if (!key.StartsWith(prefix + ":", StringComparison.Ordinal)) throw new JsonException("Wrong activation's converter.");
        return int.Parse(key.AsSpan(prefix.Length + 1), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(prefix + ":" + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
