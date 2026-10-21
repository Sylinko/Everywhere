# Mod Settings Developer Guide

Read the [Settings Specification](05-SettingsSpecification.md) for the contract and the
[main developer guide](02-ModDeveloperGuide.md) for mod lifecycle and packaging.

**Status: persistence and migrations implemented; not yet released.** SettingsEngine<TSettings> and
mod descriptor generation are available. UI registration is a separate implementation batch.

## Define a separate settings model

Define a mod-owned model using the existing notification and configuration conventions.
It does not inherit Everywhere's concrete Settings class and is not registered as that singleton.
Supply JSON metadata/options explicitly for the model and all serialized types it uses. The existing
configuration generator accepts explicit mod roots. Reference this analyzer explicitly; the SDK
does not include it or assume that a mod uses Everywhere's configuration APIs.

For a repository-based project, use an ordinary analyzer ProjectReference (adjust the relative path):

```xml
<ProjectReference Include="../../src/Everywhere.Configuration.SourceGenerator/Everywhere.Configuration.SourceGenerator.csproj"
                  ReferenceOutputAssembly="false" OutputItemType="Analyzer" />
```

If using a separately supplied generator DLL, add it through an explicit Analyzer item instead.
Importing a host installation supplies runtime API references, not this optional authoring tool.
The generator is a compile-time dependency and must not be added to the mod runtime payload.

For a UI, use the existing GeneratedSettingsItems facility or provide SettingsItems yourself.
Custom controls select dependencies at factory creation, for example:

```csharp
var control = new SettingsControl<MyControl>(context.Services);
var factory = new SettingsControl<MyControl>(
    () => new MyControl(context.Services.GetRequiredService<IMyService>()));
```

The first form requires a corresponding service registration. The second can construct the control
directly. Both defer creation until presentation. The provider overload defaults to `isCacheEnabled: false`,
so each presentation resolves the control according to its service registration. The delegate overload
defaults to `true` and retains its first result. Pass `isCacheEnabled` explicitly to choose the other
behavior. An existing-control overload always returns the supplied instance.
The presenter calls `CreateControl()` and never
supplies the application root. The factory borrows the provider; it must not dispose it. Remove UI
contributions before activation retirement and stop using their factories afterward.
Settings persistence and presentation remain separate: a mod can save settings without providing
a page, and opening a page must not create a second writer for the same file.

The standard location is:

```text
mods-data/<mod-id>/settings.json
```

Resolve it from IModContext.DataDirectory. Use the logical mod ID, not the chat-tool Key
such as mod.everything. Rebuilds and updates keep the same data directory. Do not put writable
settings in InstallationDirectory or delete them merely because the mod is unloaded.

The channel uses application APIs, so add the appropriate optional Everywhere references when using
it. Mods that only use Everywhere.Modding.Abstractions can still manage DataDirectory themselves.
There is no SettingsItems property on IMod and no UI dependency in the neutral engine.

## Generate model metadata

Use a top-level, non-generic, non-static partial provider class. JSON metadata is separate from settings
descriptors: supply a JsonSerializerContext covering the root and reachable serialized types, including
dictionary key types. Both declarations live in the mod assembly.

```csharp
[GeneratedSettingsDescriptor(typeof(SearchSettings))]
public partial class SearchSettingsDescriptors;

[JsonSerializable(typeof(SearchSettings))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
internal partial class SearchSettingsJson : JsonSerializerContext;

[GeneratedSettingsItems]
public sealed partial class SearchSettings : ObservableObject
{
    [DynamicLocaleKey("Search_IsEnabled", "Search_IsEnabledDescription")]
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public int MaxResults
    {
        get => _maxResults;
        set => SetProperty(ref _maxResults, value);
    }

    private bool _isEnabled = true;
    private int _maxResults = 100;
}
```

This uses Everywhere.Configuration, Everywhere.I18N, System.Text.Json.Serialization and
CommunityToolkit.Mvvm.ComponentModel. Define the locale keys in your mod resources and register
them through the normal mod localization path. GeneratedSettingsItems exposes the SettingsItems
property and uses compiled bindings. The locale attribute makes IsEnabled a presented setting;
MaxResults in this small example is persisted without a settings item. Apply the existing settings
item attributes to expose more fields.

## Initialize and own the engine

Import Everywhere.Configuration to use context.OpenSettingsAsync. This extension is implemented in
Everywhere.Core; using the settings channel requires an application API reference. It adds no members
or dependencies to Everywhere.Modding.Abstractions. The returned SettingsEngine<TSettings> owns the
running document; the internal SettingsInitializer is only the application's startup adapter.

Open settings once during InitializeAsync, before publishing dependent tools or UI. Retain the engine
on the mod entry. The generic type must implement INotifyPropertyChanged; it need not inherit
Everywhere's Settings. A typical entry body is:

```csharp
private SettingsEngine<SearchSettings>? _settings;

public async ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
{
    var model = new SearchSettings();
    _settings = await context.OpenSettingsAsync(
        model,
        new SemanticVersion(2, 0, 0),
        SearchSettingsJson.Default.Options,
        new SearchSettingsDescriptors(),
        configureMigrations: services => services.AddSettingsMigration<RenameSearchLimit>(),
        cancellationToken: cancellationToken);
}

public async ValueTask DisposeAsync()
{
    // Detach editors and stop and join any work that can still change the model here.
    if (_settings is not null)
    {
        await _settings.DisposeAsync();
    }
}
```

The entry implements IMod and IAsyncDisposable. The opening extension uses Everywhere.Configuration;
engine and version names use Everywhere.Configuration.Engine and Everywhere.Common; service helpers use
Microsoft.Extensions.DependencyInjection. The migration below defines RenameSearchLimit. Omit the
migration callback when no older format is supported; a fresh file starts directly at the declared
version. Use an explicit options instance and serializer context when you need custom converters.
Do not open another engine for the same document when creating a view.
The optional settingsFileName parameter selects a file under DataDirectory and defaults to settings.json;
choosing a different display entry does not select or create a different file.

Entry disposal saves on both hot unload and application exit. Merely calling context.Register on the
engine is insufficient: ordinary exit skips registered reversal. Release editors and stop model writers
before disposing the engine. Saving itself does not require a UI dispatcher.

If initialization fails, the host disposes the entry and registered resources that were created.
Do not replace an unreadable or incompatible document with defaults. Stop external writers before
flushing; a save failure is reported rather than silently treated as success. Forced termination
cannot guarantee persistence.

## Choose a presentation entry

A chat tool can override the existing ChatPlugin.SettingsItems property to expose its model's editor.
This route does not require a mod-wide registry or establish persistence ownership. A separate
optional mod-wide registry and management page are planned; their lifecycle integration is outside
this persistence batch. Document when edits take effect in your mod's operations.

## Add a migration

The existing migration class and registration helper are retained. For example, a settings format
upgrade can rename a field without depending on the current CLR settings model:

```csharp
public sealed class RenameSearchLimit : SettingsMigration, ISettingsMigrationMetadata
{
    /// <inheritdoc />
    public static SemanticVersion Version { get; } = new(2, 0, 0);

    /// <inheritdoc />
    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks =>
        [RenameProperty];

    private static bool RenameProperty(JsonObject root)
    {
        return TryMoveProperty(root, "SearchLimit", "MaxResults");
    }
}
```

This uses Everywhere.Configuration, Everywhere.Common and System.Text.Json.Nodes, plus the
normal generic collection/delegate imports. TryMoveProperty uses the existing destination replacement
behavior; handle a pre-existing destination explicitly if your migration needs a different policy.

In the OpenSettingsAsync migration-configuration callback, register it using the existing helper:

```csharp
services.AddSettingsMigration<RenameSearchLimit>();
```

Here services is the temporary migration service collection for your settings initialization, not
Everywhere's root container. Constructor dependencies and custom factories can use the existing
registration mechanism. Ordinary field transformations need neither a container dependency nor
an instance of an obsolete settings class.

Migration metadata is inspected before instances are created. Only pending migrations are resolved,
then they execute in ascending target-version order. A task returning false means that this file
needed no field change; it still completes that migration. Throw on an actual migration failure.

Do not change an already published migration to implement a later upgrade. Add another target
version, keep targets unique, and retain the chain required by your supported upgrade paths.
Operate on the old JSON shape, not assumptions taken from the current model's default values.

## Declare the settings format version

Pass the target SemanticVersion to OpenSettingsAsync separately from the mod package version. For example, mod releases 1.2.0 and 1.3.0 can both use settings format 2.0.0.
The document's Version field records the settings format; do not use it for the Everywhere version.
It is document metadata for both the application and mods, not a property of the runtime settings
model. The engine excludes it from model binding and preserves it during observed updates. A mod
model property mapped to the JSON name Version is rejected.

A new installation starts with current defaults and the declared target version without replaying
old migrations. An existing versionless file uses the initial zero version. Older supported files
run the declared chain; a current file constructs no migration instances. A file from a newer
settings format is preserved and rejected for writing rather than silently downgraded.

## Migration resources, failure and retry

Migrations run before normal observation begins. They share one temporary initialization provider,
which owns their instances and any dependencies it creates. Cleanup awaits asynchronous resources
when needed, including after failure. Borrowed application services keep their original ownership.
The standard settings opening path manages this lifetime; authors should not keep migration
instances or register migration factories in long-lived host services.

Changes are made to in-memory JSON, validated, and saved after temporary migration-resource cleanup.
An existing document is backed up before migration. A migration, validation or cleanup failure before
saving leaves that document unchanged and fails initialization. The backup is an explicit recovery
option, not an instruction to overwrite user settings automatically.

MSDI's normal DisposeAsync behavior applies: if one service throws while being disposed, disposal of
remaining services stops. The settings file is still not committed. Do not rely on another service's
disposal callback running after a failure, and do not retry a disposed provider. Prefer reliable,
self-contained migration cleanup. Register borrowed service instances directly with AddSingleton(instance)
or pass them into migration constructors without transferring their ownership; a factory returning a
borrowed disposable object can cause the temporary provider to capture and dispose it.

If migration saves successfully and a later mod initialization step fails, the new settings version
remains. The next attempt skips those completed migrations. Unload and exit do not undo migrations,
and switching back to old mod code does not roll back data. Restore a suitable backup explicitly
when recovery requires an older format.

Keep migration tasks focused on JSON transformation. Reading auxiliary data can be useful, but
external file/database writes, process startup and application registrations are not covered by
settings rollback. Tasks remain synchronous; asynchronous provider cleanup does not change the
migration task API.

## JSON metadata and unload expectations

Keep the model, serializer metadata, converters and descriptor provider within your settings engine's
lifetime. The shared dictionary-key cache uses weak Type keys and reusable delegates that
receive options per call. It must not capture your options under a permanently live host type.

Generated metadata supports predictable lifetime but is not a guarantee for arbitrary converters,
controls or third-party caches. Do not retain mod settings or migration objects in global host
collections. The host must still confirm collection; RestartRequired remains the outcome when
unloading cannot be confirmed.

Maintained tests cover isolated documents and converters, final saving and confirmed collectible
unloading through real activations, as well as migration selection, failure/retry and temporary
resource ownership. Editor lifetime checks accompany the later UI and service-boundary integrations.
